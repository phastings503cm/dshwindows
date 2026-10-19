using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - The checkpoint store
//
// checkpoints\blobs\ab\abcdef… holds each version of a file once, named by the SHA-256 of its bytes, however many turns
// it belongs to; checkpoints\turns\<chat>\<turn>.json is each turn's manifest. Pruning drops manifests older than 30
// days, then the oldest ones while the blobs take more than 1 GB, then every blob no manifest names.

/// <summary>Where checkpoints live, and the pruning of them. Thread-safe.</summary>
public sealed class CheckpointStore
{
    public const string FolderName = "checkpoints";
    /// <summary>A file larger than this isn't kept (it is recorded as too large, and can't be rewound).</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);
    public const long DefaultMaxBytes = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();

    public string Root { get; }
    private string Blobs => Path.Combine(Root, "blobs");
    private string Turns => Path.Combine(Root, "turns");

    public CheckpointStore(string root) => Root = root;

    // MARK: Blobs

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string BlobPath(string hash) => Path.Combine(Blobs, hash[..2], hash);

    /// <summary>Keep <paramref name="bytes"/> (once, however often it is put) and return its SHA-256.</summary>
    public string Put(byte[] bytes)
    {
        var hash = Hash(bytes);
        var path = BlobPath(hash);
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

    /// <summary>The bytes stored under <paramref name="hash"/>, or null when they were pruned (or never kept).</summary>
    public byte[]? Get(string hash)
    {
        if (hash.Length < 3) return null;
        try
        {
            return File.ReadAllBytes(BlobPath(hash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Has(string hash) => hash.Length > 2 && File.Exists(BlobPath(hash));

    // MARK: Manifests

    /// <summary>A file-system-safe name for an id (chat and turn ids are GUIDs; anything else is reduced to letters,
    /// digits, '-' and '_').</summary>
    private static string Safe(string id)
    {
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var safe = new string(chars);
        return safe.Length == 0 ? "_" : safe.Length > 100 ? safe[..100] : safe;
    }

    private string ManifestPath(string chatId, string turnId) => Path.Combine(Turns, Safe(chatId), Safe(turnId) + ".json");

    public void Save(TurnCheckpoint turn)
    {
        var path = ManifestPath(turn.ChatId, turn.Id);
        var json = JsonSerializer.Serialize(turn, Json);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    public TurnCheckpoint? Load(string chatId, string turnId) => Read(ManifestPath(chatId, turnId));

    private static TurnCheckpoint? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<TurnCheckpoint>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A chat's checkpoints, newest first (only turns that changed something).</summary>
    public IReadOnlyList<TurnCheckpoint> List(string chatId)
    {
        var folder = Path.Combine(Turns, Safe(chatId));
        if (!Directory.Exists(folder)) return [];
        var output = new List<TurnCheckpoint>();
        foreach (var file in SafeFiles(folder, "*.json"))
            if (Read(file) is { } turn) output.Add(turn);
        return output.OrderByDescending(t => t.At).ToList();
    }

    /// <summary>Forget a chat's checkpoints (the chat was deleted). Blobs go at the next prune.</summary>
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
                // The next prune tries again by age.
            }
        }
    }

    // MARK: Pruning

    /// <summary>What a prune removed.</summary>
    public sealed record PruneResult(int Manifests, int Blobs, long BytesFreed);

    /// <summary>Drop manifests older than <paramref name="maxAge"/>, then the oldest while the blobs they name take more than
    /// <paramref name="maxBytes"/>, then every blob no manifest names. <paramref name="now"/> is for tests.</summary>
    public PruneResult Prune(TimeSpan? maxAge = null, long? maxBytes = null, DateTimeOffset? now = null)
    {
        var age = maxAge ?? DefaultMaxAge;
        var cap = maxBytes ?? DefaultMaxBytes;
        var at = now ?? DateTimeOffset.Now;
        lock (_lock)
        {
            var manifests = new List<(string Path, TurnCheckpoint Turn)>();
            if (Directory.Exists(Turns))
                foreach (var file in SafeFiles(Turns, "*.json", recursive: true))
                    if (Read(file) is { } turn) manifests.Add((file, turn));
                    else TryDelete(file); // damaged: nothing can be rewound from it
            var removed = 0;
            foreach (var old in manifests.Where(m => at - (m.Turn.FinishedAt ?? m.Turn.At) > age).ToList())
            {
                TryDelete(old.Path);
                manifests.Remove(old);
                removed++;
            }
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var file in Directory.Exists(Blobs) ? SafeFiles(Blobs, "*", recursive: true) : [])
            {
                if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                try
                {
                    sizes[Path.GetFileName(file)] = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone already.
                }
            }
            static IEnumerable<string> Named(TurnCheckpoint turn) =>
                turn.Files.SelectMany(f => new[] { f.Before, f.After }).Concat(turn.RewoundFrom?.Values ?? Enumerable.Empty<string?>()).OfType<string>();
            long Total() => manifests.SelectMany(m => Named(m.Turn)).Distinct(StringComparer.Ordinal).Sum(h => sizes.GetValueOrDefault(h));
            var ordered = manifests.OrderBy(m => m.Turn.FinishedAt ?? m.Turn.At).ToList();
            while (ordered.Count > 0 && Total() > cap)
            {
                TryDelete(ordered[0].Path);
                manifests.Remove(ordered[0]);
                ordered.RemoveAt(0);
                removed++;
            }
            var named = manifests.SelectMany(m => Named(m.Turn)).ToHashSet(StringComparer.Ordinal);
            var blobs = 0;
            long freed = 0;
            foreach (var (hash, size) in sizes)
            {
                if (named.Contains(hash) || hash.Length < 3) continue;
                if (TryDelete(BlobPath(hash)))
                {
                    blobs++;
                    freed += size;
                }
            }
            return new PruneResult(removed, blobs, freed);
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
