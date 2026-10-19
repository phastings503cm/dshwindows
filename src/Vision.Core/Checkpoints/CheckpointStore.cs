using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - The checkpoint store
//
// Under the data folder, the same layout as Vision for macOS:
//   checkpoints\objects\<2 hex>\<sha-256 hex>     each version of a file once, however many turns keep it
//   checkpoints\turns\<chat id>\<turn id>.json    one record per turn that changed something
// Pruning drops records older than 30 days (and empty ones older than an hour), then the oldest while the contents they
// keep take more than 1 GB (never the newest), then every content no record — and no turn still recording — names.
// Contents written in the last hour are never removed: they may belong to a turn whose record isn't saved yet.

/// Where checkpoints live, and the pruning of them. Thread-safe.
public sealed class CheckpointStore
{
    public const string FolderName = "checkpoints";
    /// A file larger than this isn't kept: a turn that changes it can't rewind it.
    public const long MaxFileBytes = 16L * 1024 * 1024;
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);
    public const long DefaultMaxBytes = 1024L * 1024 * 1024;
    /// Contents and empty records younger than this are left alone by pruning.
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Iso8601SecondsConverter() },
    };

    private readonly Lock _lock = new();
    /// Turns still recording: their contents are in use even before their record is saved.
    private readonly HashSet<TurnRecorder> _live = [];

    public string Root { get; }
    private string Objects => Path.Combine(Root, "objects");
    private string Turns => Path.Combine(Root, "turns");

    public CheckpointStore(string root) => Root = root;

    // MARK: Contents

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string ObjectPath(string hash) => Path.Combine(Objects, hash[..2], hash);

    /// Keep <paramref name="bytes"/> (once, however often it is put) and return its SHA-256.
    public string Put(byte[] bytes)
    {
        var hash = Hash(bytes);
        var path = ObjectPath(hash);
        lock (_lock)
        {
            if (File.Exists(path)) return hash;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        return hash;
    }

    /// The bytes stored under <paramref name="hash"/>, or null when they were pruned (or never kept).
    public byte[]? Get(string hash)
    {
        if (hash.Length < 3) return null;
        try
        {
            return File.ReadAllBytes(ObjectPath(hash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Has(string hash) => hash.Length > 2 && File.Exists(ObjectPath(hash));

    internal void Track(TurnRecorder recorder, bool live)
    {
        lock (_lock)
        {
            if (live) _live.Add(recorder);
            else _live.Remove(recorder);
        }
    }

    // MARK: Records

    /// A file name for an id (ids are GUIDs; anything else is reduced to letters, digits, '-', '_' and '.').
    private static string Safe(string id)
    {
        var safe = new string(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return safe.Length == 0 || safe.StartsWith('.') ? "_" + safe : safe;
    }

    private string RecordPath(string chatId, string turnId) => Path.Combine(Turns, Safe(chatId), Safe(turnId) + ".json");

    public void Save(TurnCheckpoint turn)
    {
        var path = RecordPath(turn.ChatId, turn.Id);
        var json = JsonSerializer.Serialize(turn, Json);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    public TurnCheckpoint? Load(string chatId, string turnId) => Read(RecordPath(chatId, turnId));

    private static TurnCheckpoint? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<TurnCheckpoint>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// A chat's turns that changed files, newest first.
    public IReadOnlyList<TurnCheckpoint> List(string chatId)
    {
        var folder = Path.Combine(Turns, Safe(chatId));
        if (!Directory.Exists(folder)) return [];
        var output = new List<TurnCheckpoint>();
        foreach (var file in SafeFiles(folder, "*.json"))
            if (Read(file) is { Changed.Count: > 0 } turn) output.Add(turn);
        return output.OrderByDescending(t => t.StartedAt).ToList();
    }

    /// A deleted chat's records go with it (their contents go at the next prune).
    public void DeleteChat(string chatId)
    {
        var folder = Path.Combine(Turns, Safe(chatId));
        lock (_lock)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next prune takes it by age.
            }
        }
    }

    // MARK: Pruning

    /// What a prune removed, and what the contents still kept take.
    public sealed record PruneResult(int Records, int Objects, long BytesKept);

    /// See the top of the file. <paramref name="now"/> is for tests.
    public PruneResult Prune(TimeSpan? maxAge = null, long? maxBytes = null, DateTimeOffset? now = null)
    {
        var age = maxAge ?? DefaultMaxAge;
        var cap = maxBytes ?? DefaultMaxBytes;
        var at = now ?? DateTimeOffset.Now;
        lock (_lock)
        {
            var kept = new List<(string Path, TurnCheckpoint Turn)>();
            var removed = 0;
            foreach (var file in Directory.Exists(Turns) ? SafeFiles(Turns, "*.json", recursive: true) : [])
            {
                var turn = Read(file);
                var old = turn is null || at - turn.UpdatedAt > age || (turn.Changed.Count == 0 && at - turn.UpdatedAt > Grace);
                if (old && TryDelete(file)) removed++;
                else if (!old) kept.Add((file, turn!));
            }
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            var recent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.Exists(Objects) ? SafeFiles(Objects, "*", recursive: true) : [])
            {
                if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                try
                {
                    var info = new FileInfo(file);
                    sizes[info.Name] = info.Length;
                    if (at - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) < Grace) recent.Add(info.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone already.
                }
            }
            var inMemory = _live.Select(r => r.Snapshot).ToList();
            HashSet<string> Referenced(IEnumerable<TurnCheckpoint> turns) =>
                turns.Concat(inMemory).SelectMany(t => t.Files).SelectMany(f => new[] { f.Before, f.After }).OfType<string>().ToHashSet(StringComparer.Ordinal);
            // Newest first; the oldest go while over budget (the newest always stays).
            kept = kept.OrderByDescending(k => k.Turn.UpdatedAt).ToList();
            var live = Referenced(kept.Select(k => k.Turn));
            long Total(HashSet<string> hashes) => hashes.Sum(h => sizes.GetValueOrDefault(h));
            var total = Total(live);
            while (total > cap && kept.Count > 1)
            {
                var oldest = kept[^1];
                kept.RemoveAt(kept.Count - 1);
                if (TryDelete(oldest.Path)) removed++;
                live = Referenced(kept.Select(k => k.Turn));
                total = Total(live);
            }
            var objects = 0;
            foreach (var (hash, _) in sizes)
            {
                if (live.Contains(hash) || recent.Contains(hash) || hash.Length < 3) continue;
                if (TryDelete(ObjectPath(hash))) objects++;
            }
            return new PruneResult(removed, objects, total);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern, bool recursive = false)
    {
        try
        {
            return Directory.EnumerateFiles(folder, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
