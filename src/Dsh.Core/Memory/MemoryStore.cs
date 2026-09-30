using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Core;

/// <summary>What to store. The store fills in the id and dates.</summary>
public sealed record MemoryDraft
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Kind { get; init; } = MemoryKinds.Note;
    public string? Project { get; init; }
    public bool Pinned { get; init; }
    public string Source { get; init; } = "agent";
    public DateTimeOffset? CreatedAt { get; init; }
}

public enum MemorySaveKind { Created, Updated }

/// <summary>Whether a save made a new note or folded into one that already said the same thing.</summary>
public sealed record MemorySaveResult(MemoryItem Item, MemorySaveKind Kind);

public sealed record MemoryQueryOptions
{
    /// <summary>The project the chat is in: its notes and the global ones are searched, other projects' are not.</summary>
    public string? Project { get; init; }
    public int Limit { get; init; } = 4;
    public double MinRelevance { get; init; } = 0.15;
    /// <summary>Pinned notes live in the system prompt, so a per-message search leaves them out.</summary>
    public bool IncludePinned { get; init; }
    /// <summary>Search every project (the memory manager's search box).</summary>
    public bool AllProjects { get; init; }
    /// <summary>List whatever matches at all, however common the words are — for someone browsing, who wants
    /// "tests" to find the notes that mention tests. (Recall wants the opposite: only what is distinctive.)</summary>
    public bool Browse { get; init; }
}

public sealed record MemoryStats(int Count, int Pinned, long Characters, long FileBytes);

/// <summary>The long-term memory: notes in an append-only log (<c>memories.jsonl</c>), searched through an
/// in-memory BM25 index. Loading is a single sequential read; the index is built the first time it is
/// needed and kept current by every change, so a search costs a few dictionary lookups however many
/// notes there are. Thread-safe; shared by the UI and every engine.</summary>
public sealed class MemoryStore
{
    public const string FileName = "memories.jsonl";
    public const int MaxTitleLength = 160;
    public const int MaxBodyLength = 6_000;
    public const int MaxTags = 8;
    /// <summary>The most terms of a query the store looks at before the index picks the rarest of them.</summary>
    private const int MaxQueryTerms = 512;
    /// <summary>Past this many notes the least valuable unpinned ones are pruned.</summary>
    public const int DefaultMaxItems = 5_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class LogLine
    {
        [JsonPropertyName("op")] public string Op { get; set; } = "";
        public MemoryItem? Item { get; set; }
        public string? Id { get; set; }
        public List<string>? Ids { get; set; }
        public DateTimeOffset? At { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, MemoryItem> _items = new(StringComparer.Ordinal);
    private readonly MemoryIndex _index = new();
    private readonly TimeProvider _clock;
    private readonly int _maxItems;
    private bool _indexed;
    private int _revision;
    private int _logLines;
    /// <summary>Lines of the log this version could not make sense of (other software's, damaged, from a newer version).</summary>
    private int _skipped;
    /// <summary>The file ends in the middle of a line (a write a crash cut off): the next append starts on a new one.</summary>
    private bool _needsNewline;
    /// <summary>A write failed: memory is ahead of the disk, and the next write must save everything.</summary>
    private bool _dirty;
    /// <summary>Reading the log failed: nothing may be written over it.</summary>
    private bool _loadFailed;
    private bool _backedUp;
    private List<LogLine>? _batch;

    /// <param name="directory">Where <c>memories.jsonl</c> lives; <c>%APPDATA%\DSH\memory</c> by default.</param>
    public MemoryStore(string? directory = null, TimeProvider? clock = null, int maxItems = DefaultMaxItems)
    {
        Directory = directory ?? Path.Combine(AppPaths.Root, "memory");
        FilePath = Path.Combine(Directory, FileName);
        _clock = clock ?? TimeProvider.System;
        _maxItems = Math.Max(10, maxItems);
        Load();
    }

    public string Directory { get; }
    public string FilePath { get; }

    /// <summary>Set when the log couldn't be read or written; the store keeps working in memory.</summary>
    public string? StorageProblem { get; private set; }

    /// <summary>Bumped on every change to the notes' content (not on usage counts).</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Raised after a change, on the thread that made it.</summary>
    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_lock) return _items.Count;
        }
    }

    public MemoryStats Stats()
    {
        lock (_lock)
        {
            long bytes = 0;
            try
            {
                if (File.Exists(FilePath)) bytes = new FileInfo(FilePath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Size is informational.
            }
            return new MemoryStats(_items.Count, _items.Values.Count(i => i.Pinned),
                _items.Values.Sum(i => (long)i.Title.Length + i.Body.Length), bytes);
        }
    }

    // MARK: Reading

    /// <summary>Every note, most recently changed first.</summary>
    public IReadOnlyList<MemoryItem> All()
    {
        lock (_lock) return _items.Values.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public MemoryItem? Get(string id)
    {
        lock (_lock) return _items.GetValueOrDefault(id.Trim().Trim('[', ']'));
    }

    /// <summary>Standing facts for the system prompt: pinned notes that are global or belong to <paramref name="project"/>.</summary>
    public IReadOnlyList<MemoryItem> Pinned(string? project)
    {
        lock (_lock)
        {
            return _items.Values.Where(i => i.Pinned && (i.Project is null || SameProject(i.Project, project)))
                .OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Notes matching <paramref name="query"/>, best first. Cheap: an index lookup, no scan.</summary>
    public IReadOnlyList<MemoryHit> Search(string query, MemoryQueryOptions? options = null)
    {
        options ??= new MemoryQueryOptions();
        // (Every term goes to the index, which keeps the rare ones of a long query; cutting it to the longest words here
        // would drop the one distinctive name in a pasted stack trace.)
        var terms = MemoryText.QueryTerms(query, MaxQueryTerms);
        if (terms.Count == 0) return [];
        lock (_lock)
        {
            EnsureIndexLocked();
            var now = _clock.GetUtcNow();
            double WeightOf(string id)
            {
                if (!_items.TryGetValue(id, out var item)) return 0;
                if (item.Pinned && !options.IncludePinned) return 0;
                var scope = 1.0;
                if (item.Project is not null && !options.AllProjects)
                {
                    if (!SameProject(item.Project, options.Project)) return 0;
                    scope = 1.15; // about this project: worth a little more
                }
                var age = Math.Max(0, (now - (item.LastUsedAt ?? item.UpdatedAt)).TotalDays);
                var recency = 0.8 + 0.2 * Math.Exp(-age / 120.0);
                var usage = Math.Min(1.2, 1 + 0.05 * Math.Log(1 + item.UseCount));
                return scope * recency * usage;
            }
            return _index.Search(terms, options.Limit, WeightOf, options.Browse ? 0 : options.MinRelevance, applyFloor: !options.Browse)
                .Select(hit => new MemoryHit(_items[hit.Id], hit.Score, hit.Relevance)).ToList();
        }
    }

    // MARK: Writing

    /// <summary>Store a note. One that says nearly the same thing as an existing note in the same scope
    /// updates it instead of piling up beside it.</summary>
    public MemorySaveResult Save(MemoryDraft draft)
    {
        MemorySaveResult result;
        lock (_lock)
        {
            EnsureIndexLocked();
            result = SaveLocked(Clean(draft), dedupeBySimilarity: true);
            EnforceCapLocked();
        }
        Announce();
        return result;
    }

    /// <summary>Store many notes (an import). Re-importing the same source updates rather than duplicates:
    /// a note with the same source and title in the same scope is replaced.</summary>
    public int Import(IEnumerable<MemoryDraft> drafts) => Import(drafts, out _);

    /// <summary>The same, and says how many notes were left out because they look like keys or passwords — a
    /// file someone else wrote can hold one, and notes are stored as plain text and sent to the model.</summary>
    public int Import(IEnumerable<MemoryDraft> drafts, out int skippedAsSecrets)
    {
        var added = 0;
        skippedAsSecrets = 0;
        lock (_lock)
        {
            EnsureIndexLocked();
            _batch = [];
            try
            {
                var cleaned = drafts.Select(Clean).Where(d => d.Body.Length > 0).ToList();
                var flagged = FlagSecrets(cleaned);
                for (var i = 0; i < cleaned.Count; i++)
                {
                    if (flagged[i])
                    {
                        skippedAsSecrets++;
                        continue;
                    }
                    if (SaveLocked(cleaned[i], dedupeBySimilarity: false).Kind == MemorySaveKind.Created) added++;
                }
                EnforceCapLocked();
            }
            finally
            {
                var lines = _batch;
                _batch = null;
                WriteLines(lines); // one write for the lot, not one per note
            }
        }
        Announce();
        return added;
    }

    /// <summary>Which of these drafts look like credentials — alone, or read together with the piece beside them: a file cut
    /// into pieces can cut a password from its name ("…the password is" | "hunter2hunter2…"), and neither piece looks like a
    /// secret on its own. The title and the text are read as one ("Staging DB password: Xk29mQ788abZ"); the tags are read apart
    /// from them (a text that ends on "the password" must not take a tag for its value).</summary>
    internal static bool[] FlagSecrets(IReadOnlyList<MemoryDraft> drafts)
    {
        var flagged = new bool[drafts.Count];
        for (var i = 0; i < drafts.Count; i++)
            flagged[i] = SecretGuard.LooksLikeSecret(drafts[i].Title + ": " + drafts[i].Body) || SecretGuard.LooksLikeSecret(string.Join('\n', drafts[i].Tags));
        for (var i = 0; i + 1 < drafts.Count; i++)
        {
            if (flagged[i] || flagged[i + 1] || drafts[i].Source != drafts[i + 1].Source) continue;
            if (SecretGuard.LooksLikeSecret(drafts[i].Body + " " + drafts[i + 1].Body)) flagged[i] = flagged[i + 1] = true;
        }
        return flagged;
    }

    /// <summary>Whether any note satisfies <paramref name="predicate"/>.</summary>
    public bool Any(Func<MemoryItem, bool> predicate)
    {
        lock (_lock) return _items.Values.Any(predicate);
    }

    /// <summary>How many notes satisfy <paramref name="predicate"/>.</summary>
    public int CountWhere(Func<MemoryItem, bool> predicate)
    {
        lock (_lock) return _items.Values.Count(predicate);
    }

    /// <summary>Change a note (the memory manager's edit box). Null when there is no such note.</summary>
    public MemoryItem? Update(string id, Func<MemoryItem, MemoryItem> edit)
    {
        MemoryItem? updated = null;
        lock (_lock)
        {
            EnsureIndexLocked();
            if (!_items.TryGetValue(id.Trim().Trim('[', ']'), out var current)) return null;
            var edited = edit(current) with { Id = current.Id, CreatedAt = current.CreatedAt, UpdatedAt = _clock.GetUtcNow() };
            var cleaned = Clean(new MemoryDraft
            {
                Title = edited.Title, Body = edited.Body, Tags = edited.Tags, Kind = edited.Kind,
                Project = edited.Project, Pinned = edited.Pinned, Source = edited.Source,
            });
            updated = edited with
            {
                Title = cleaned.Title, Body = cleaned.Body, Tags = cleaned.Tags, Kind = cleaned.Kind, Project = cleaned.Project,
            };
            PutLocked(updated);
        }
        Announce();
        return updated;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = DeleteLocked(id.Trim().Trim('[', ']'));
        }
        if (removed) Announce();
        return removed;
    }

    /// <summary>Delete every note <paramref name="predicate"/> picks; how many went.</summary>
    public int DeleteWhere(Func<MemoryItem, bool> predicate)
    {
        int count;
        lock (_lock)
        {
            var doomed = _items.Values.Where(predicate).Select(i => i.Id).ToList();
            foreach (var id in doomed) DeleteLocked(id);
            count = doomed.Count;
        }
        if (count > 0) Announce();
        return count;
    }

    /// <summary>Record that notes were used (shown to the model). Never throws.</summary>
    public void NoteUsed(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            var used = new List<string>();
            var now = _clock.GetUtcNow();
            foreach (var id in ids)
            {
                if (!_items.TryGetValue(id, out var item)) continue;
                _items[id] = item with { LastUsedAt = now, UseCount = item.UseCount + 1 };
                used.Add(id);
            }
            if (used.Count > 0) Append(new LogLine { Op = "use", Ids = used, At = now });
        }
    }

    /// <summary>Rewrite the log with only the live notes (drops replaced and deleted lines).</summary>
    public void Compact()
    {
        lock (_lock) CompactLocked();
    }

    // MARK: Internals

    private static MemoryDraft Clean(MemoryDraft draft)
    {
        var body = draft.Body.Trim().Replace("\r\n", "\n");
        if (body.Length > MaxBodyLength) body = MemoryText.Head(body, MaxBodyLength).TrimEnd() + "…";
        var title = MemoryText.Clip(draft.Title.Length > 0 ? draft.Title : body, MaxTitleLength);
        var tags = draft.Tags.Where(t => t is not null).Select(t => t.Trim().ToLowerInvariant().Replace(' ', '-'))
            .Where(t => t.Length > 0).Select(t => MemoryText.Head(t, 30)).Distinct(StringComparer.Ordinal).Take(MaxTags).ToList();
        var project = string.IsNullOrWhiteSpace(draft.Project) ? null : NormalizeProject(draft.Project);
        return draft with { Title = title, Body = body, Tags = tags, Kind = MemoryKinds.Normalize(draft.Kind), Project = project };
    }

    public static string NormalizeProject(string path)
    {
        var trimmed = path.Trim();
        return trimmed.Length > 1 ? trimmed.TrimEnd('\\', '/') : trimmed;
    }

    private static bool SameProject(string? a, string? b) =>
        a is not null && b is not null && string.Equals(NormalizeProject(a), NormalizeProject(b), StringComparison.OrdinalIgnoreCase);

    private MemorySaveResult SaveLocked(MemoryDraft draft, bool dedupeBySimilarity)
    {
        var now = _clock.GetUtcNow();
        var existing = FindDuplicateLocked(draft, dedupeBySimilarity);
        if (existing is not null)
        {
            var merged = existing with
            {
                Title = draft.Title,
                Body = draft.Body,
                Tags = existing.Tags.Concat(draft.Tags).Distinct(StringComparer.Ordinal).Take(MaxTags).ToList(),
                Kind = draft.Kind,
                Pinned = existing.Pinned || draft.Pinned,
                // Whoever wrote the new words owns the note now (the agent can only ever reach its own).
                Source = draft.Source,
                UpdatedAt = now,
            };
            PutLocked(merged);
            return new MemorySaveResult(merged, MemorySaveKind.Updated);
        }
        var item = new MemoryItem
        {
            Id = NewIdLocked(),
            Title = draft.Title,
            Body = draft.Body,
            Tags = draft.Tags,
            Kind = draft.Kind,
            Project = draft.Project,
            Pinned = draft.Pinned,
            Source = draft.Source,
            CreatedAt = draft.CreatedAt ?? now,
            UpdatedAt = now,
        };
        PutLocked(item);
        return new MemorySaveResult(item, MemorySaveKind.Created);
    }

    /// <summary>How alike two notes with the same title must be to count as one note.</summary>
    private const double SameTitleSimilarity = 0.4;

    /// <summary>An existing note in the same scope that this is the same note as: for an import, the same source and
    /// title; for a fresh save, one that says roughly the same (a generic title like "Testing" is shared by notes about
    /// different things, and saving one must not overwrite another).</summary>
    private MemoryItem? FindDuplicateLocked(MemoryDraft draft, bool bySimilarity)
    {
        bool SameScope(MemoryItem i) => (i.Project is null && draft.Project is null) || SameProject(i.Project, draft.Project);
        var text = draft.Title + " " + draft.Body;
        foreach (var item in _items.Values)
        {
            if (!SameScope(item) || !string.Equals(item.Title, draft.Title, StringComparison.OrdinalIgnoreCase)) continue;
            if (!bySimilarity)
            {
                if (string.Equals(item.Source, draft.Source, StringComparison.Ordinal)) return item;
                continue;
            }
            if (MayFoldInto(draft, item) && (draft.Body.Length == 0 || Alike(text, item, SameTitleSimilarity))) return item;
        }
        if (!bySimilarity || draft.Body.Length == 0) return null;
        foreach (var hit in _index.Search(MemoryText.QueryTerms(draft.Title + " " + draft.Body, 12), 3, id => SameScope(_items[id]) && MayFoldInto(draft, _items[id]) ? 1 : 0, 0))
        {
            var candidate = _items[hit.Id];
            if (MayFoldInto(draft, candidate) && Alike(text, candidate, NearlyIdentical)) return candidate;
        }
        return null;
    }

    /// <summary>Whether a fresh save may fold into <paramref name="existing"/> rather than sit beside it. Not into a
    /// copy of a file or an import (those are rewritten from their source), and not — when the agent is the one saving —
    /// into anything the user wrote or pinned: what the agent read on the way may have put those words in its mouth,
    /// and a pinned note is part of the system prompt.</summary>
    private static bool MayFoldInto(MemoryDraft draft, MemoryItem existing)
    {
        if (existing.Source.StartsWith("file:", StringComparison.Ordinal) || existing.Source.StartsWith("import:", StringComparison.Ordinal)) return false;
        if (string.Equals(draft.Source, "agent", StringComparison.Ordinal)
            && (!string.Equals(existing.Source, "agent", StringComparison.Ordinal) || existing.Pinned))
            return false;
        return true;
    }

    /// <summary>The same claim, near enough: alike in wording, and not differing in a number ("Node 3" / "Node 4") or in
    /// being turned around by a "not" — which the word matching would otherwise not see. Below a high bar, only a rewrite
    /// that kept everything the old note said (or said all the new one does) counts: "run staging first" and "run
    /// production first" share most of their words and are two different notes.</summary>
    private static bool Alike(string text, MemoryItem existing, double threshold)
    {
        var other = existing.Title + " " + existing.Body;
        if (MemoryText.Contradicts(text, other)) return false;
        var similarity = MemoryText.Similarity(text, other);
        return similarity >= NearlyIdentical || (similarity >= threshold && MemoryText.Covers(text, other));
    }

    private const double NearlyIdentical = 0.75;

    private string NewIdLocked()
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!_items.ContainsKey(id)) return id;
        }
    }

    private void PutLocked(MemoryItem item)
    {
        _items[item.Id] = item;
        if (_indexed) _index.Add(item);
        Interlocked.Increment(ref _revision);
        Append(new LogLine { Op = "put", Item = item });
    }

    private bool DeleteLocked(string id)
    {
        if (!_items.Remove(id)) return false;
        if (_indexed) _index.Remove(id);
        Interlocked.Increment(ref _revision);
        Append(new LogLine { Op = "del", Id = id });
        return true;
    }

    /// <summary>Drop the least valuable unpinned notes once the store is over its cap: rarely used, long
    /// untouched ones go first.</summary>
    private void EnforceCapLocked()
    {
        if (_items.Count <= _maxItems) return;
        var now = _clock.GetUtcNow();
        // What someone wrote by hand is worth more than a copy of a file that still exists (a mirrored MEMORY.md, an import).
        double Worth(MemoryItem i) => i.UseCount * 5.0 + 1.0 / (1 + Math.Max(0, (now - (i.LastUsedAt ?? i.UpdatedAt)).TotalDays) / 60.0)
                                      + (i.Source == "user" ? 6.0 : i.Source == "agent" ? 1.0 : 0.0);
        var surplus = _items.Count - (int)(_maxItems * 0.95);
        foreach (var victim in _items.Values.Where(i => !i.Pinned).OrderBy(Worth).Take(surplus).Select(i => i.Id).ToList())
            DeleteLocked(victim);
    }

    // MARK: Persistence

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var lastUnderstood = true;
                foreach (var line in File.ReadLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    _logLines++;
                    lastUnderstood = ReadLine(line);
                    if (!lastUnderstood) _skipped++;
                }
                var unfinished = EndsMidLine();
                // A last line that never got its newline and makes no sense is a write a crash cut off — not damage, and
                // not worth keeping: the rewrite below drops it, and it doesn't count against the file.
                var torn = unfinished && !lastUnderstood;
                if (torn) _skipped--;
                // (If the rewrite below fails, the fragment is still there: the next append must start on a fresh line.)
                _needsNewline = unfinished;
                // Mostly stale lines: rewrite once, on load, so the log doesn't grow without bound. Never when part of the
                // file was not understood — it may belong to software that is newer than this, and a rewrite would erase it.
                if (torn || (_logLines > 400 && _logLines > _items.Count * 3 && _skipped == 0)) CompactLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only part of the file may have been read. Nothing is written over it (see CompactLocked), so the notes that
                // did not load are still on the disk for the next launch.
                _loadFailed = true;
                _skipped = Math.Max(_skipped, 1);
                StorageProblem = $"Couldn't read the memory file: {ex.Message}";
            }
        }
    }

    /// <summary>Apply one log line; false when it isn't one this version understands.</summary>
    private bool ReadLine(string line)
    {
        LogLine? entry;
        try
        {
            entry = JsonSerializer.Deserialize<LogLine>(line, Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
        if (entry is null) return false;
        switch (entry.Op)
        {
            case "put" when entry.Item is { Id.Length: > 0 } item:
                // A hand-edited line can say null where a string belongs (or in a list).
                _items[item.Id] = item with
                {
                    Title = item.Title ?? "", Body = item.Body ?? "", Source = item.Source ?? "agent",
                    Tags = (item.Tags ?? []).Where(t => t is not null).ToList(),
                    Kind = MemoryKinds.Normalize(item.Kind),
                };
                return true;
            case "del" when entry.Id is not null:
                _items.Remove(entry.Id);
                return true;
            case "use" when entry.Ids is not null:
                foreach (var id in entry.Ids)
                {
                    if (id is not null && _items.TryGetValue(id, out var used))
                        _items[id] = used with { UseCount = used.UseCount + 1, LastUsedAt = entry.At ?? used.LastUsedAt };
                }
                return true;
            default:
                return false;
        }
    }

    private bool EndsMidLine()
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private void EnsureIndexLocked()
    {
        if (_indexed) return;
        foreach (var item in _items.Values) _index.Add(item);
        _indexed = true;
    }

    private void Append(LogLine line)
    {
        if (_batch is not null)
        {
            _batch.Add(line);
            return;
        }
        WriteLines([line]);
    }

    /// <summary>Add lines to the end of the log. A write that keeps failing (a virus scanner holding the file) is
    /// remembered, and the next write saves everything instead of only its own lines — nothing is quietly lost.</summary>
    private void WriteLines(IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0) return;
        if (_dirty && !_loadFailed)
        {
            CompactLocked(); // the whole store, these lines included
            return;
        }
        var body = string.Concat(lines.Select(l => JsonSerializer.Serialize(l, Json) + "\n"));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(FilePath, _needsNewline ? "\n" + body : body, TextUtil.Utf8NoBom);
                _logLines += lines.Count;
                _needsNewline = false;
                if (!_loadFailed) StorageProblem = null;
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A write that failed part-way leaves half a line: whatever comes next starts on a new one.
                _needsNewline = true;
                if (attempt < 3)
                {
                    Thread.Sleep(40 * attempt);
                    continue;
                }
                _dirty = true;
                StorageProblem = $"Couldn't save to the memory file: {ex.Message}";
                return;
            }
        }
    }

    private void CompactLocked()
    {
        // The file could not be read (all of it, at least): what is in memory is not the whole story, and replacing the file
        // with it would erase the rest.
        if (_loadFailed) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // Lines this version couldn't read go with the old file: keep it, once, before it is replaced.
            if (_skipped > 0 && !_backedUp && File.Exists(FilePath))
            {
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
                _backedUp = true;
            }
            var temp = FilePath + ".tmp";
            var lines = _items.Values.OrderBy(i => i.CreatedAt)
                .Select(i => JsonSerializer.Serialize(new LogLine { Op = "put", Item = i }, Json));
            WriteDurably(temp, string.Join("\n", lines) + (_items.Count > 0 ? "\n" : ""));
            File.Move(temp, FilePath, overwrite: true);
            _logLines = _items.Count;
            _skipped = 0;
            _needsNewline = false;
            _dirty = false;
            StorageProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StorageProblem = $"Couldn't tidy the memory file: {ex.Message}";
        }
    }

    /// <summary>Write and flush to the disk, so a power cut right after the swap can't leave an empty file.</summary>
    private static void WriteDurably(string path, string text)
    {
        var bytes = TextUtil.Utf8NoBom.GetBytes(text);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private void Announce() => Changed?.Invoke(this, EventArgs.Empty);
}
