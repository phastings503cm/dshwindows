using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Core;

// MARK: - Credential vault
//
// API keys, tokens and passwords the agent may use without ever seeing them.
//
// - Values are encrypted at rest with Windows Data Protection (DPAPI, current user), all of them in
//   one file (vault.bin). Everything else — name, description, tags, a SHA-256 fingerprint — lives
//   in a plain JSON file (vault.json), so searching the vault never touches a secret.
// - The model finds credentials with `vault_search` (names, descriptions, fingerprints; never
//   values) and writes `{{vault:NAME}}` wherever a value belongs in a tool call — a shell command, a
//   file it writes, a URL or header. The engine substitutes the real value just before the tool runs
//   and scrubs every vault value out of every tool result, so a secret never reaches the model, the
//   transcript, or the logs.
// - Each credential says whether the agent may use it freely, must ask first (once per chat), or
//   may never use it.
// - The user opens the vault to browse, search, reveal ("show the value behind the fingerprint"),
//   copy, edit and delete.

/// <summary>What kind of secret a credential is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VaultKind>))]
public enum VaultKind
{
    [JsonStringEnumMemberName("apiKey")] ApiKey,
    [JsonStringEnumMemberName("token")] Token,
    [JsonStringEnumMemberName("password")] Password,
    [JsonStringEnumMemberName("other")] Other,
}

/// <summary>What the agent may do with a credential.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VaultAccess>))]
public enum VaultAccess
{
    /// <summary>Use it whenever a task needs it.</summary>
    [JsonStringEnumMemberName("allowed")] Allowed,
    /// <summary>Ask the user the first time a chat uses it.</summary>
    [JsonStringEnumMemberName("ask")] Ask,
    /// <summary>Never hand it to the agent (it can still see that it exists).</summary>
    [JsonStringEnumMemberName("never")] Never,
}

public static class VaultLabels
{
    public static string Label(this VaultKind kind) => kind switch
    {
        VaultKind.ApiKey => "API key",
        VaultKind.Token => "Token",
        VaultKind.Password => "Password",
        _ => "Other",
    };

    public static string Label(this VaultAccess access) => access switch
    {
        VaultAccess.Allowed => "Agent may use",
        VaultAccess.Ask => "Ask first",
        _ => "Never",
    };
}

// MARK: - Entries

/// <summary>One credential's details — everything except its value, which only the blob store holds.
/// Edit a copy with <c>with { … }</c> and hand it to <see cref="CredentialVault.Update"/>.</summary>
public sealed record VaultEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("D").ToUpperInvariant();
    /// <summary>The handle the agent writes: <c>{{vault:NAME}}</c>. Letters, digits, <c>_</c>, <c>-</c>, <c>.</c>.</summary>
    public string Name { get; init; } = "";
    public VaultKind Kind { get; init; } = VaultKind.ApiKey;
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Account / login the credential belongs to, when there is one.</summary>
    public string? Username { get; init; }
    /// <summary>Where it's used ("https://api.openai.com").</summary>
    public string? Url { get; init; }
    /// <summary>"sha256:1a2b3c4d5e6f" — identifies the value without revealing it.</summary>
    public string Fingerprint { get; init; } = "";
    public VaultAccess Access { get; init; } = VaultAccess.Allowed;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; init; }
    public int UseCount { get; init; }

    /// <summary>The placeholder the agent writes to use this credential.</summary>
    [JsonIgnore]
    public string Placeholder => "{{vault:" + Name + "}}";

    /// <summary>Every word a search can match.</summary>
    internal string Haystack =>
        string.Join(" ", new[] { Name, Kind.Label(), Description, Username ?? "", Url ?? "", Fingerprint }.Concat(Tags))
            .ToLowerInvariant();

    // Value equality over the tags too (a record compares lists by reference), like the Swift
    // struct's Hashable conformance.
    public bool Equals(VaultEntry? other) =>
        other is not null
        && Id == other.Id && Name == other.Name && Kind == other.Kind && Description == other.Description
        && Tags.SequenceEqual(other.Tags) && Username == other.Username && Url == other.Url
        && Fingerprint == other.Fingerprint && Access == other.Access && CreatedAt == other.CreatedAt
        && UpdatedAt == other.UpdatedAt && LastUsedAt == other.LastUsedAt && UseCount == other.UseCount;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Fingerprint, UseCount);
}

// MARK: - Errors

public enum VaultErrorKind { InvalidName, DuplicateName, EmptyValue, NotFound, Storage }

/// <summary>A vault operation that failed. <see cref="Exception.Message"/> is written for the user;
/// every failure — including the blob store's and the metadata file's — surfaces as this type.</summary>
public sealed class VaultException : Exception
{
    public VaultErrorKind Kind { get; }
    /// <summary>The credential name involved, when there is one.</summary>
    public string? Name { get; }

    private VaultException(VaultErrorKind kind, string message, string? name = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Name = name;
    }

    public static VaultException InvalidName(string name) =>
        new(VaultErrorKind.InvalidName, $"“{name}” isn't a usable name — use letters, digits, _ - or . (e.g. OPENAI_API_KEY).", name);

    public static VaultException DuplicateName(string name) =>
        new(VaultErrorKind.DuplicateName, $"There is already a credential named {name}.", name);

    public static VaultException EmptyValue() => new(VaultErrorKind.EmptyValue, "The secret value is empty.");

    public static VaultException NotFound(string name) => new(VaultErrorKind.NotFound, $"No credential named {name}.", name);

    /// <summary>The blob store or the metadata file failed (the Mac app's "Keychain refused").</summary>
    public static VaultException Storage(string message, Exception? inner = null) =>
        new(VaultErrorKind.Storage, message, inner: inner);
}

// MARK: - Blob stores

/// <summary>Where the secret values live: one opaque blob holding all of them. The app encrypts it
/// with DPAPI (<see cref="DpapiBlobStore"/>); tests keep it in memory (<see cref="MemoryBlobStore"/>).
/// Implementations may throw; the vault reports any failure as <see cref="VaultException"/>.</summary>
public interface ISecretBlobStore
{
    /// <summary>The blob, or null when nothing has been saved yet.</summary>
    byte[]? Load();
    void Save(byte[] data);
}

/// <summary>Values in memory only (tests).</summary>
public sealed class MemoryBlobStore : ISecretBlobStore
{
    private readonly Lock _lock = new();
    private byte[]? _data;

    public byte[]? Load()
    {
        lock (_lock) return _data is null ? null : [.. _data];
    }

    public void Save(byte[] data)
    {
        lock (_lock) _data = [.. data];
    }
}

// MARK: - Lookup

/// <summary>What the engine learns when it looks a credential up.</summary>
public abstract record VaultLookup
{
    /// <summary>The credential exists and has a value; <paramref name="Access"/> says whether the agent may use it.</summary>
    public sealed record Value(string Secret, VaultAccess Access) : VaultLookup
    {
        // Never print the secret (a record's ToString lists every member, and lookups end up in
        // debugger views, test failures and logs).
        public override string ToString() => $"Value {{ Access = {Access} }}";
    }

    /// <summary>No credential by that name, or it has no stored value.</summary>
    public sealed record Missing : VaultLookup;
}

// MARK: - The vault

/// <summary>The credential vault: metadata in vault.json, values in one encrypted blob. Thread-safe;
/// shared by the UI and every engine (and subagent).</summary>
public sealed class CredentialVault
{
    public const string MetadataFileName = "vault.json";
    /// <summary>Names are at most this long.</summary>
    public const int MaxNameLength = 80;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        WriteIndented = true,
    };

    private readonly Lock _lock = new();
    private readonly ISecretBlobStore _store;
    private readonly List<VaultEntry> _entries;
    /// <summary>Decrypted values by entry id, loaded from the store on first need.</summary>
    private Dictionary<string, string>? _values;
    private int _revision;

    /// <param name="directory">Where vault.json lives; <see cref="AppPaths.Root"/> by default.</param>
    /// <param name="store">Where the values live; a <see cref="DpapiBlobStore"/> writing vault.bin
    /// next to vault.json by default.</param>
    public CredentialVault(string? directory = null, ISecretBlobStore? store = null)
    {
        StorageDirectory = directory ?? AppPaths.Root;
        MetadataPath = Path.Combine(StorageDirectory, MetadataFileName);
        _store = store ?? new DpapiBlobStore(Path.Combine(StorageDirectory, DpapiBlobStore.DefaultFileName));
        _entries = LoadEntries(MetadataPath);
    }

    public string StorageDirectory { get; }
    public string MetadataPath { get; }

    /// <summary>Bumped on every change (the UI and the system prompt follow it).</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Raised after every change — add, edit, delete, and each use the agent makes of a
    /// credential. It fires on the thread that made the change, often an engine thread, so the UI
    /// must marshal to its dispatcher.</summary>
    public event EventHandler? Changed;

    // MARK: Reading

    /// <summary>Every credential, sorted by name (case-insensitively).</summary>
    public IReadOnlyList<VaultEntry> All
    {
        get
        {
            lock (_lock) return _entries.OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_lock) return _entries.Count == 0;
        }
    }

    /// <summary>The credential called <paramref name="name"/> (case-insensitive), or null.</summary>
    public VaultEntry? Entry(string name)
    {
        lock (_lock) return EntryLocked(name);
    }

    /// <summary>Entries matching every word of <paramref name="query"/> (name, kind, description, tags,
    /// username, URL, fingerprint). An empty query matches everything.</summary>
    public IReadOnlyList<VaultEntry> Search(string query)
    {
        var words = Words(query.ToLowerInvariant());
        return All.Where(entry => words.All(w => entry.Haystack.Contains(w, StringComparison.Ordinal))).ToList();
    }

    /// <summary>A query's words: split on whitespace and commas, empty pieces dropped.</summary>
    private static List<string> Words(string query)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= query.Length; i++)
        {
            var separator = i == query.Length || char.IsWhiteSpace(query[i]) || query[i] == ',';
            if (separator && start >= 0)
            {
                words.Add(query[start..i]);
                start = -1;
            }
            else if (!separator && start < 0)
            {
                start = i;
            }
        }
        return words;
    }

    /// <summary>The secret value (for the engine's substitution and the UI's reveal), or null.</summary>
    /// <exception cref="VaultException">The store couldn't be read or decrypted.</exception>
    public string? Value(string id)
    {
        lock (_lock)
        {
            LoadValuesLocked();
            return _values!.GetValueOrDefault(id);
        }
    }

    /// <summary>Look a credential up by the name the agent wrote.</summary>
    public VaultLookup Lookup(string name)
    {
        lock (_lock)
        {
            if (EntryLocked(name) is not { } entry) return new VaultLookup.Missing();
            try
            {
                LoadValuesLocked();
            }
            catch (VaultException)
            {
                return new VaultLookup.Missing();
            }
            return _values!.GetValueOrDefault(entry.Id) is { Length: > 0 } value
                ? new VaultLookup.Value(value, entry.Access)
                : new VaultLookup.Missing();
        }
    }

    /// <summary>Every (name, value) pair, longest value first — for scrubbing tool output. Values
    /// shorter than four characters are left out (they would mangle ordinary text). Empty, without
    /// touching the store, when the vault is empty.</summary>
    public IReadOnlyList<(string Name, string Value)> ValuesForRedaction()
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return [];
            try
            {
                LoadValuesLocked();
            }
            catch (VaultException)
            {
                return [];
            }
            var values = _values!;
            return _entries
                .Select(e => (e.Name, Value: values.GetValueOrDefault(e.Id)))
                .Where(p => p.Value is not null && new StringInfo(p.Value).LengthInTextElements >= 4)
                .Select(p => (p.Name, Value: p.Value!))
                // Longest first, so a value that contains another is replaced whole.
                .OrderByDescending(p => p.Value.Length)
                .ToList();
        }
    }

    // MARK: Writing

    /// <summary>Add a credential. Tags are trimmed, lowercased and de-duplicated; a blank username or
    /// URL is dropped.</summary>
    /// <exception cref="VaultException">Invalid or duplicate name, empty value, or the store failed.</exception>
    public VaultEntry Add(string name, string value, VaultKind kind = VaultKind.ApiKey, string description = "",
                          IEnumerable<string>? tags = null, string? username = null, string? url = null,
                          VaultAccess access = VaultAccess.Allowed)
    {
        name = name.Trim();
        if (!IsValidName(name)) throw VaultException.InvalidName(name);
        if (value.Length == 0) throw VaultException.EmptyValue();
        return Change(() =>
        {
            if (_entries.Any(e => SameName(e.Name, name))) throw VaultException.DuplicateName(name);
            LoadValuesLocked();
            var now = DateTimeOffset.Now;
            var entry = new VaultEntry
            {
                Name = name,
                Kind = kind,
                Description = description,
                Tags = Clean(tags ?? []),
                Username = NilIfBlank(username),
                Url = NilIfBlank(url),
                Fingerprint = Fingerprint(value),
                Access = access,
                CreatedAt = now,
                UpdatedAt = now,
            };
            SaveValuesLocked(new Dictionary<string, string>(_values!, StringComparer.Ordinal) { [entry.Id] = value });
            _entries.Add(entry);
            PersistLocked();
            return entry;
        });
    }

    /// <summary>Update a credential's details, and its value when <paramref name="value"/> is given.
    /// The fingerprint, use count and dates stay the vault's own: a stale copy from an open window
    /// can't roll back uses the agent made meanwhile, or claim a fingerprint for a value it didn't set.</summary>
    /// <exception cref="VaultException">Invalid or duplicate name, empty value, unknown id, or the store failed.</exception>
    public void Update(VaultEntry updated, string? value = null)
    {
        var name = updated.Name.Trim();
        if (!IsValidName(name)) throw VaultException.InvalidName(name);
        if (value is { Length: 0 }) throw VaultException.EmptyValue();
        Change(() =>
        {
            var i = _entries.FindIndex(e => e.Id == updated.Id);
            if (i < 0) throw VaultException.NotFound(updated.Name);
            if (_entries.Any(e => e.Id != updated.Id && SameName(e.Name, name))) throw VaultException.DuplicateName(name);
            var stored = _entries[i];
            var entry = updated with
            {
                Name = name,
                Tags = Clean(updated.Tags),
                Username = NilIfBlank(updated.Username),
                Url = NilIfBlank(updated.Url),
                Fingerprint = stored.Fingerprint,
                CreatedAt = stored.CreatedAt,
                UpdatedAt = DateTimeOffset.Now,
                LastUsedAt = stored.LastUsedAt,
                UseCount = stored.UseCount,
            };
            if (value is not null)
            {
                LoadValuesLocked();
                SaveValuesLocked(new Dictionary<string, string>(_values!, StringComparer.Ordinal) { [entry.Id] = value });
                entry = entry with { Fingerprint = Fingerprint(value) };
            }
            _entries[i] = entry;
            PersistLocked();
        });
    }

    /// <summary>Delete a credential and its value. An unknown id is ignored.</summary>
    /// <exception cref="VaultException">The store failed.</exception>
    public void Delete(string id)
    {
        Change(() =>
        {
            var i = _entries.FindIndex(e => e.Id == id);
            if (i < 0) return;
            LoadValuesLocked();
            var values = new Dictionary<string, string>(_values!, StringComparer.Ordinal);
            values.Remove(id);
            SaveValuesLocked(values);
            _entries.RemoveAt(i);
            PersistLocked();
        });
    }

    /// <summary>Record that the agent used a credential. Never throws: a failed save only loses the count.</summary>
    public void NoteUse(string name)
    {
        Change(() =>
        {
            var i = _entries.FindIndex(e => SameName(e.Name, name));
            if (i < 0) return;
            _entries[i] = _entries[i] with { LastUsedAt = DateTimeOffset.Now, UseCount = _entries[i].UseCount + 1 };
            try
            {
                PersistLocked();
            }
            catch (VaultException)
            {
                // Best effort, like the Mac app's `try?`.
            }
        });
    }

    // MARK: Helpers

    /// <summary>"sha256:" + the first 12 hex digits of the value's SHA-256 (of its UTF-8 bytes).</summary>
    public static string Fingerprint(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexStringLower(digest.AsSpan(0, 6));
    }

    /// <summary>1–80 ASCII letters, digits, <c>_</c>, <c>-</c> or <c>.</c> — exactly what a
    /// <c>{{vault:NAME}}</c> placeholder can spell.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxNameLength && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private VaultEntry? EntryLocked(string name) => _entries.FirstOrDefault(e => SameName(e.Name, name));

    private static List<string> Clean(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0 && seen.Add(t)).ToList();
    }

    private static string? NilIfBlank(string? s)
    {
        var trimmed = s?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Run a mutation under the lock, then tell observers — outside the lock, so a handler
    /// may read the vault — whenever the revision moved (even if a later step threw).</summary>
    private T Change<T>(Func<T> body)
    {
        var before = Revision;
        try
        {
            lock (_lock) return body();
        }
        finally
        {
            if (Revision != before) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Change(Action body) => Change(() =>
    {
        body();
        return true;
    });

    private static List<VaultEntry> LoadEntries(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return [];
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        try
        {
            return (JsonSerializer.Deserialize<List<VaultEntry?>>(bytes, Json) ?? []).OfType<VaultEntry>().ToList();
        }
        catch (JsonException)
        {
            // Start empty, as the Mac app does — but keep the damaged file: the next save would
            // otherwise destroy the only record of which encrypted value is which.
            try
            {
                File.Copy(path, path + ".damaged", overwrite: true);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
            return [];
        }
    }

    private void LoadValuesLocked()
    {
        if (_values is not null) return;
        byte[]? data;
        try
        {
            data = _store.Load();
        }
        catch (Exception ex) when (ex is not VaultException)
        {
            throw VaultException.Storage($"Couldn't open the credential vault: {ex.Message}", ex);
        }
        _values = data is null ? new Dictionary<string, string>(StringComparer.Ordinal) : DecodeValues(data);
    }

    private static Dictionary<string, string> DecodeValues(byte[] data)
    {
        try
        {
            var decoded = JsonSerializer.Deserialize<Dictionary<string, string?>>(data) ?? [];
            return decoded.Where(p => p.Value is not null)
                .ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveValuesLocked(Dictionary<string, string> values)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            _store.Save(data);
        }
        catch (Exception ex) when (ex is not VaultException)
        {
            throw VaultException.Storage($"Couldn't save the credential vault: {ex.Message}", ex);
        }
        _values = values;
    }

    private void PersistLocked()
    {
        Interlocked.Increment(ref _revision);
        var data = JsonSerializer.SerializeToUtf8Bytes(_entries, Json);
        try
        {
            VaultFiles.WriteAtomic(MetadataPath, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw VaultException.Storage($"Couldn't save {MetadataFileName}: {ex.Message}", ex);
        }
    }
}

// MARK: - Files

internal static class VaultFiles
{
    /// <summary>Write <paramref name="data"/> to a temporary file beside <paramref name="path"/>,
    /// flush it to disk, then move it into place — a crash never leaves a half-written vault, and a
    /// power cut can't persist the rename before the bytes.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }
}
