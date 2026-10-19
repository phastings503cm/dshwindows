using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Vision.Core;

/// <summary>Where the other tools keep their chats. <see cref="Standard"/> is the real profile; tests and the UI self-test
/// point it at a scratch folder.</summary>
/// <param name="Home">The user's profile folder.</param>
/// <param name="ClaudeConfigDir">Claude Code's CLAUDE_CONFIG_DIR, when it was moved off ~\.claude.</param>
/// <param name="CodexHome">Codex's CODEX_HOME, when it was moved off ~\.codex.</param>
/// <param name="OpenClawStateDirs">OpenClaw's state folders (null: the ones in <paramref name="Home"/>).</param>
public sealed record ChatImportLocations(string Home, string? ClaudeConfigDir = null, string? CodexHome = null, IReadOnlyList<string>? OpenClawStateDirs = null)
{
    public static ChatImportLocations Standard => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetEnvironmentVariable("CODEX_HOME"));

    public string ClaudeProjects => Path.Combine(string.IsNullOrWhiteSpace(ClaudeConfigDir) ? Path.Combine(Home, ".claude") : ClaudeConfigDir, "projects");

    public string Codex => string.IsNullOrWhiteSpace(CodexHome) ? Path.Combine(Home, ".codex") : CodexHome;

    public IReadOnlyList<string> OpenClaw => OpenClawStateDirs ?? OpenClawChats.StateDirs(Home, Environment.GetEnvironmentVariable("OPENCLAW_STATE_DIR"));
}

/// <summary>Finds the chats on this PC for the preview — fast after the first time: what a file held is remembered by its
/// size and time (<see cref="ChatScanCache"/>), so only new or grown sessions are read again.</summary>
public static class ChatImportScanner
{
    /// <summary>Files read at once (the machine is shared; the scan is I/O and JSON, not worth every core).</summary>
    private static int Parallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public static IReadOnlyList<ChatCandidate> ClaudeCode(ChatImportLocations locations, ChatScanCache? cache = null, IProgress<string>? progress = null,
                                                          CancellationToken cancellationToken = default)
    {
        var files = ClaudeCodeChats.SessionFiles(locations.ClaudeProjects);
        return ScanFiles(files, "Claude Code", cache, progress, cancellationToken,
            (path, summary, token) => ClaudeCodeChats.Read(path, summary, token));
    }

    public static IReadOnlyList<ChatCandidate> Codex(ChatImportLocations locations, ChatScanCache? cache = null, IProgress<string>? progress = null,
                                                     CancellationToken cancellationToken = default)
    {
        var files = CodexChats.SessionFiles(locations.Codex);
        var names = CodexChats.ThreadNames(locations.Codex);
        // A renamed thread is a changed title: the names file's stamp is part of each session's.
        var namesStamp = Stamp(Path.Combine(locations.Codex, "session_index.jsonl"));
        return ScanFiles(files, "Codex", cache, progress, cancellationToken,
            (path, summary, token) => CodexChats.Read(path, names, summary, token), extraStamp: namesStamp);
    }

    public static IReadOnlyList<ChatCandidate> OpenClaw(ChatImportLocations locations, ChatScanCache? cache = null, IProgress<string>? progress = null,
                                                        CancellationToken cancellationToken = default)
    {
        var found = new List<ChatCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in locations.OpenClaw)
        {
            foreach (var store in OpenClawChats.Stores(state))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (store.IsSqlite)
                {
                    if (!SqliteReader.IsAvailable) continue;
                    IReadOnlyList<OpenClawChats.SqliteSession> sessions;
                    try
                    {
                        sessions = OpenClawChats.ListSqlite(store.Path, store.Agent);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    var wanted = sessions;
                    var done = 0;
                    foreach (var session in wanted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report($"Reading OpenClaw sessions… {++done} of {wanted.Count}");
                        var key = $"openclaw:{store.Path}#{session.Id}";
                        var stamp = session.Updated?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? "";
                        var candidate = Summarize(key, stamp, cache, store.Path,
                            () => OpenClawChats.ReadSqlite(store.Path, session, summaryOnly: true, cancellationToken),
                            token => OpenClawChats.ReadSqlite(store.Path, session, false, token));
                        if (candidate is not null && seen.Add(candidate.SourceId)) found.Add(candidate);
                    }
                }
                else
                {
                    var files = OpenClawChats.ListJsonl(store.Path);
                    var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
                    var listed = ScanFiles(files.Select(f => f.Path).ToList(), "OpenClaw", cache, progress, cancellationToken, (path, summary, token) =>
                    {
                        var (_, key, label) = byPath[path];
                        return OpenClawChats.ReadJsonl(path, key, label, summary, token, store.Agent);
                    });
                    foreach (var candidate in listed)
                        if (seen.Add(candidate.SourceId)) found.Add(candidate);
                }
            }
        }
        return found.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    /// <summary>Everything on this PC: Claude Code, Codex and OpenClaw.</summary>
    public static IReadOnlyList<ChatCandidate> Local(ChatImportLocations locations, ChatScanCache? cache = null, IProgress<string>? progress = null,
                                                     CancellationToken cancellationToken = default) =>
        [.. ClaudeCode(locations, cache, progress, cancellationToken), .. Codex(locations, cache, progress, cancellationToken), .. OpenClaw(locations, cache, progress, cancellationToken)];

    // MARK: - Files

    private static IReadOnlyList<ChatCandidate> ScanFiles(IReadOnlyList<string> files, string what, ChatScanCache? cache, IProgress<string>? progress,
                                                          CancellationToken cancellationToken, Func<string, bool, CancellationToken, ImportedChat?> read,
                                                          string extraStamp = "")
    {
        var found = new ConcurrentBag<ChatCandidate>();
        var done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, path =>
        {
            var count = Interlocked.Increment(ref done);
            if (count == 1 || count % 10 == 0 || count == files.Count) progress?.Report($"Reading {what} sessions… {count} of {files.Count}");
            var candidate = Summarize("file:" + path, Stamp(path) + extraStamp, cache, path,
                () => read(path, true, cancellationToken), token => read(path, false, token));
            if (candidate is not null) found.Add(candidate);
        });
        return found.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    /// <summary>The preview's line for one chat: remembered when its stamp is unchanged, else read (summarised) and remembered.
    /// A file that can't be read, or holds no conversation, gives nothing.</summary>
    private static ChatCandidate? Summarize(string key, string stamp, ChatScanCache? cache, string location, Func<ImportedChat?> summarize,
                                            Func<CancellationToken, ImportedChat?> load)
    {
        Task<ImportedChat?> Load(CancellationToken token) => Task.Run(() => load(token), token);
        if (cache?.TryGet(key, stamp) is { } remembered)
            return remembered.Messages == 0 ? null : remembered.ToCandidate(location, Load);
        ImportedChat? chat;
        try
        {
            chat = summarize();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
        cache?.Put(key, stamp, chat);
        return chat?.ToCandidate(location, Load);
    }

    /// <summary>"length:ticks" — changes when a file grows or is rewritten.</summary>
    internal static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}

/// <summary>What each scanned chat held, by where it was read and a stamp of its state — so a scan only reads what changed.
/// Kept in the data folder as <c>import\chat-scan.json</c>; titles and counts only, no message text.</summary>
public sealed class ChatScanCache
{
    /// <summary>Raised when what a summary holds changes, so old entries are read again.</summary>
    private const int Version = 3;

    public sealed record Entry
    {
        public string Stamp { get; init; } = "";
        public string Source { get; init; } = "";
        public string SourceId { get; init; } = "";
        public string Title { get; init; } = "";
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public int Messages { get; init; }
        public string? Project { get; init; }
        public string? Note { get; init; }
        public bool Automated { get; init; }

        internal ChatCandidate ToCandidate(string location, Func<CancellationToken, Task<ImportedChat?>> load) => new()
        {
            Source = ChatSources.FromRaw(Source) ?? ChatSource.Vision,
            SourceId = SourceId,
            Title = Title,
            StartedAt = StartedAt,
            UpdatedAt = UpdatedAt,
            Messages = Messages,
            Project = Project,
            Location = location,
            Note = Note,
            Automated = Automated,
            Load = load,
        };
    }

    private sealed class Stored
    {
        public int Version { get; set; }
        public Dictionary<string, Entry>? Entries { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private readonly string? _path;
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private bool _changed;

    private ChatScanCache(string? path, IDictionary<string, Entry> entries)
    {
        _path = path;
        _entries = new ConcurrentDictionary<string, Entry>(entries, StringComparer.Ordinal);
    }

    /// <summary>The cache in <paramref name="path"/> (empty when there is none, or it is from another version).</summary>
    public static ChatScanCache Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new ChatScanCache(path, new Dictionary<string, Entry>());
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options);
            if (stored?.Version == Version && stored.Entries is { } entries) return new ChatScanCache(path, entries);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged cache is a cold one.
        }
        return new ChatScanCache(path, new Dictionary<string, Entry>());
    }

    /// <summary>The usual place: the data folder's import folder.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.Root, "import", "chat-scan.json");

    public Entry? TryGet(string key, string stamp) =>
        stamp.Length > 0 && _entries.TryGetValue(key, out var entry) && entry.Stamp == stamp ? entry : null;

    /// <summary>Remember what a read found (null: nothing to import there, so it is skipped quickly next time).</summary>
    public void Put(string key, string stamp, ImportedChat? chat)
    {
        if (stamp.Length == 0) return;
        _entries[key] = chat is null
            ? new Entry { Stamp = stamp }
            : new Entry
            {
                Stamp = stamp,
                Source = chat.Source.RawValue(),
                SourceId = chat.SourceId,
                Title = chat.Title,
                StartedAt = chat.StartedAt,
                UpdatedAt = chat.UpdatedAt,
                Messages = chat.Messages,
                Project = chat.Project,
                Note = chat.Notes.Count > 0 ? chat.Notes[0] : null,
                Automated = chat.Automated,
            };
        _changed = true;
    }

    public void Save()
    {
        if (_path is null || !_changed) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored { Version = Version, Entries = new Dictionary<string, Entry>(_entries) }, Options), TextUtil.Utf8NoBom);
            File.Move(temp, _path, overwrite: true);
            _changed = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not remembered: the next scan reads again.
        }
    }
}

/// <summary>Writes chosen chats into the conversation log as real chats, skipping any already there.</summary>
public static class ChatImporter
{
    /// <summary>What is already here: every imported chat's source and id, and every chat's own id (as "vision:&lt;id&gt;", so a
    /// Vision chat saved in Guardian is recognised on the PC it came from).</summary>
    public static HashSet<string> ImportedKeys(ConversationLog log)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in log.List())
        {
            keys.Add(ChatCandidate.KeyOf(ChatSource.Vision.RawValue(), session.Id));
            if (session.Imported is { Source.Length: > 0, Id.Length: > 0 } origin) keys.Add(origin.Key);
        }
        return keys;
    }

    /// <summary>The candidates with <see cref="ChatCandidate.Status"/> set against what the log holds.</summary>
    public static IReadOnlyList<ChatCandidate> WithStatus(IEnumerable<ChatCandidate> candidates, ISet<string> keys) =>
        candidates.Select(c => c with { Status = keys.Contains(c.Key) ? ChatImportStatus.Imported : ChatImportStatus.New }).ToList();

    /// <summary>Store one chat. Returns its new id, or null when it is already here (the same source and id, or — for a Vision
    /// chat coming back — a chat with its id). Call on the thread that owns the log.</summary>
    public static string? Write(ConversationLog log, ImportedChat chat, ISet<string>? keys = null, DateTimeOffset? now = null)
    {
        keys ??= ImportedKeys(log);
        var key = ChatCandidate.KeyOf(chat.Source.RawValue(), chat.SourceId);
        if (keys.Contains(key) || chat.KeepId is { } taken && log.Contains(taken)) return null;
        var id = chat.KeepId is { } keep && GuardianChats.IsSafeId(keep) ? keep : Guid.NewGuid().ToString();
        var folderHere = chat.Project is { Length: > 0 } project && Directory.Exists(project);
        var at = now ?? DateTimeOffset.Now;
        var rows = new List<LogItemRow>();
        // A Vision chat coming back (a JSON export, or one saved in Guardian) is its rows as they were.
        if (chat.KeepId is null)
            rows.Add(new LogItemRow(id, 0, "notice", Header(chat, folderHere, at), null, null, null, false, chat.Rows.FirstOrDefault()?.At ?? chat.StartedAt ?? chat.UpdatedAt));
        rows.AddRange(chat.Rows);
        var session = new LogSession
        {
            Id = id,
            Title = chat.Title,
            Cwd = folderHere ? chat.Project : null,
            Preset = PermissionPreset.WorkspaceWrite.RawValue(),
            UpdatedAt = chat.UpdatedAt,
            Imported = chat.Source == ChatSource.Vision && chat.KeepId is not null
                ? null // the same Vision chat, back on this PC
                : new ChatOrigin
                {
                    Source = chat.Source.RawValue(),
                    Id = chat.SourceId,
                    From = chat.From,
                    Project = chat.Project,
                    Model = chat.Model,
                    StartedAt = chat.StartedAt,
                    UpdatedAt = chat.UpdatedAt,
                    Messages = chat.Messages,
                    ImportedAt = at,
                },
        };
        log.Import(session, rows);
        keys.Add(key);
        keys.Add(ChatCandidate.KeyOf(ChatSource.Vision.RawValue(), id));
        return id;
    }

    /// <summary>The notice an imported chat opens with — the same words as Vision for macOS: "Imported from Claude Code on Oct
    /// 19, 2026: 412 messages, Oct 3, 2026 – Oct 5, 2026, in C:\code\shop. It ran 3 subagents whose own transcripts stayed in
    /// Claude Code. Any model can carry on from here: …". Notices aren't sent to the model.</summary>
    public static string Header(ImportedChat chat, bool folderHere, DateTimeOffset importedAt)
    {
        static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        var messages = chat.Messages;
        var first = chat.Rows.FirstOrDefault()?.At ?? chat.StartedAt ?? chat.UpdatedAt;
        var last = chat.Rows.LastOrDefault()?.At ?? chat.UpdatedAt;
        var span = Day(first) == Day(last) ? Day(first) : $"{Day(first)} – {Day(last)}";
        var text = $"Imported from {chat.Source.Label()} on {Day(importedAt)}: {messages} message{(messages == 1 ? "" : "s")}, {span}";
        if (chat.Project is { Length: > 0 } cwd)
            text += folderHere ? $", in {cwd}" : $", in {cwd} (not on this computer, so it continues without a project)";
        text += ".";
        if (chat.Subagents > 0)
            text += $" It ran {chat.Subagents} subagent{(chat.Subagents == 1 ? "" : "s")} whose own transcripts stayed in {chat.Source.Label()}.";
        return text + " Any model can carry on from here: earlier tool calls are shown as cards and reach the model as a written summary.";
    }

    /// <summary>Read and store the chosen chats, one at a time (reading off this thread, writing on it). One that can't be
    /// read is reported, never the end of the rest.</summary>
    public static async Task<ChatImportResult> ImportAsync(ConversationLog log, IReadOnlyList<ChatCandidate> chosen, IProgress<string>? progress = null,
                                                           CancellationToken cancellationToken = default)
    {
        var keys = ImportedKeys(log);
        var imported = new List<string>();
        var failed = new List<(ChatCandidate, string)>();
        int already = 0, messages = 0, done = 0;
        foreach (var candidate in chosen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Importing {++done} of {chosen.Count}: {candidate.Title}");
            if (keys.Contains(candidate.Key))
            {
                already++;
                continue;
            }
            ImportedChat? chat;
            try
            {
                chat = candidate.Load is null ? null : await candidate.Load(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or GuardianError or InvalidDataException or HttpRequestException)
            {
                failed.Add((candidate, error.Message));
                continue;
            }
            if (chat is null)
            {
                failed.Add((candidate, "It has no messages to import."));
                continue;
            }
            if (Write(log, chat, keys) is { } id)
            {
                imported.Add(id);
                messages += chat.Messages;
            }
            else already++;
        }
        return new ChatImportResult { Imported = imported, AlreadyThere = already, Failed = failed, Messages = messages };
    }
}
