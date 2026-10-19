using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vision.Core;

// MARK: - MCP servers: what is configured
//
// Settings › MCP Servers keeps the list in mcp-servers.json in the data folder: how to reach each server (a command to run
// for stdio, a URL for Streamable HTTP), whether it is on, whether its tools ask before each use, and the project it
// belongs to (null = every chat). Environment variables and HTTP headers are listed by name only: their values are in
// Credential Manager (Vision/mcp/<id>/env/<NAME>, Vision/mcp/<id>/header/<Name>), never in the file. Vision for macOS
// keeps the same file, with its values in the Keychain.

[JsonConverter(typeof(JsonStringEnumConverter<McpTransportKind>))]
public enum McpTransportKind
{
    /// <summary>A program Vision starts, spoken to over its standard input and output.</summary>
    [JsonStringEnumMemberName("stdio")] Stdio,
    /// <summary>Streamable HTTP: JSON-RPC posted to one URL, answered with JSON or an event stream.</summary>
    [JsonStringEnumMemberName("http")] Http,
    /// <summary>The older HTTP + server-sent events transport (Vision tries Streamable HTTP at the same URL first).</summary>
    [JsonStringEnumMemberName("sse")] Sse,
}

/// <summary>One configured MCP server.</summary>
public sealed record McpServerConfig
{
    /// <summary>Unique, and the prefix of its tools' names (mcp__&lt;id&gt;__&lt;tool&gt;): letters, digits, '-' and '_'.</summary>
    public string Id { get; set; } = "";
    /// <summary>The name shown (what the source called it).</summary>
    public string Name { get; set; } = "";
    public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    /// <summary>Where the program runs (null = the chat's project folder, or the data folder for a chat without one).</summary>
    public string? Cwd { get; set; }
    public string? Url { get; set; }
    /// <summary>The environment variables it is given; values in Credential Manager.</summary>
    public List<string> EnvKeys { get; set; } = [];
    /// <summary>The HTTP headers it is sent; values in Credential Manager.</summary>
    public List<string> HeaderKeys { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>Its tools ask before they run (once per tool per chat). Off: they run without asking.</summary>
    public bool AskBeforeUse { get; set; } = true;
    /// <summary>The project folder it belongs to (from a project's .mcp.json); null = every chat.</summary>
    public string? Project { get; set; }
    /// <summary>Where it was imported from ("Claude Code", "Claude Desktop", "Cursor", "Project .mcp.json"); null = added here.</summary>
    public string? Source { get; set; }
    public string? SourceFile { get; set; }

    [JsonIgnore] public string Target => Transport == McpTransportKind.Stdio
        ? string.Join(" ", new[] { Command ?? "" }.Concat(Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))).Trim()
        : Url ?? "";

    public McpServerConfig Copy() => this with { Args = [.. Args], EnvKeys = [.. EnvKeys], HeaderKeys = [.. HeaderKeys] };

    public static string EnvTarget(string id, string key) => $"{SecretStore.Prefix}mcp/{id}/env/{key}";
    public static string HeaderTarget(string id, string name) => $"{SecretStore.Prefix}mcp/{id}/header/{name}";

    /// <summary>A server id from a name: lowercased letters, digits, '-' and '_' ("GitHub Server" → "github-server").</summary>
    public static string IdFrom(string name)
    {
        var id = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        return id.Length == 0 ? "server" : id.Length > 32 ? id[..32].TrimEnd('-') : id;
    }

    /// <summary>Whether this server is offered in a chat working in <paramref name="project"/> (null = a chat without one).</summary>
    public bool AppliesTo(string? project) =>
        Project is null || (project is not null && string.Equals(Normalize(Project), Normalize(project),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('\\', '/');
        }
    }
}

/// <summary>mcp-servers.json, and the secrets that go with it.</summary>
public sealed class McpServerStore
{
    public const string FileName = "mcp-servers.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class StoredFile
    {
        public int Version { get; set; } = 1;
        public List<McpServerConfig> Servers { get; set; } = [];
    }

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<McpServerConfig> _servers = [];

    public McpServerStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Raised after the list changes (on the thread that changed it).</summary>
    public event Action? Changed;

    public IReadOnlyList<McpServerConfig> Servers
    {
        get
        {
            lock (_lock) return _servers.Select(s => s.Copy()).ToList();
        }
    }

    public McpServerConfig? Find(string id)
    {
        lock (_lock) return _servers.FirstOrDefault(s => s.Id == id)?.Copy();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_path), Json);
            lock (_lock) _servers = stored?.Servers.Where(s => s.Id.Length > 0).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Copy(_path, _path + ".damaged", overwrite: true); } catch (Exception) { /* best effort */ }
        }
    }

    private void Save()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(new StoredFile { Servers = _servers }, Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this session.
        }
        Changed?.Invoke();
    }

    /// <summary>An id no other server has: <paramref name="wanted"/>, else "wanted-2", "wanted-3"…</summary>
    public string UniqueId(string wanted, string? except = null)
    {
        var id = McpServerConfig.IdFrom(wanted);
        lock (_lock)
        {
            bool Taken(string candidate) => _servers.Any(s => s.Id == candidate && s.Id != except);
            if (!Taken(id)) return id;
            for (var n = 2; ; n++)
                if (!Taken($"{id}-{n}")) return $"{id}-{n}";
        }
    }

    /// <summary>Add a server, or replace the one with its id. <paramref name="env"/> and <paramref name="headers"/> (when given)
    /// replace its secret values: their names go in the file, their values in Credential Manager.</summary>
    public void Save(McpServerConfig server, IReadOnlyDictionary<string, string>? env = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var copy = server.Copy();
        McpServerConfig? old;
        lock (_lock) old = _servers.FirstOrDefault(s => s.Id == copy.Id);
        if (env is not null)
        {
            foreach (var key in old?.EnvKeys.Except(env.Keys) ?? []) TryDelete(McpServerConfig.EnvTarget(copy.Id, key));
            foreach (var (key, value) in env) TryWrite(McpServerConfig.EnvTarget(copy.Id, key), value);
            copy.EnvKeys = env.Keys.ToList();
        }
        if (headers is not null)
        {
            foreach (var key in old?.HeaderKeys.Except(headers.Keys) ?? []) TryDelete(McpServerConfig.HeaderTarget(copy.Id, key));
            foreach (var (key, value) in headers) TryWrite(McpServerConfig.HeaderTarget(copy.Id, key), value);
            copy.HeaderKeys = headers.Keys.ToList();
        }
        lock (_lock)
        {
            var index = _servers.FindIndex(s => s.Id == copy.Id);
            if (index >= 0) _servers[index] = copy; else _servers.Add(copy);
        }
        Save();
    }

    public void SetEnabled(string id, bool enabled) => Update(id, s => s.Enabled = enabled);
    public void SetAskBeforeUse(string id, bool ask) => Update(id, s => s.AskBeforeUse = ask);

    private void Update(string id, Action<McpServerConfig> change)
    {
        lock (_lock)
        {
            if (_servers.FirstOrDefault(s => s.Id == id) is not { } server) return;
            change(server);
        }
        Save();
    }

    /// <summary>Forget a server and its secrets.</summary>
    public void Remove(string id)
    {
        McpServerConfig? old;
        lock (_lock)
        {
            old = _servers.FirstOrDefault(s => s.Id == id);
            if (old is null) return;
            _servers.Remove(old);
        }
        foreach (var key in old.EnvKeys) TryDelete(McpServerConfig.EnvTarget(id, key));
        foreach (var key in old.HeaderKeys) TryDelete(McpServerConfig.HeaderTarget(id, key));
        Save();
    }

    /// <summary>The server's environment variables with their values (read from Credential Manager; a missing one is empty).</summary>
    public static Dictionary<string, string> EnvOf(McpServerConfig server) =>
        server.EnvKeys.ToDictionary(k => k, k => TryRead(McpServerConfig.EnvTarget(server.Id, k)) ?? "", StringComparer.Ordinal);

    public static Dictionary<string, string> HeadersOf(McpServerConfig server) =>
        server.HeaderKeys.ToDictionary(k => k, k => TryRead(McpServerConfig.HeaderTarget(server.Id, k)) ?? "", StringComparer.OrdinalIgnoreCase);

    private static string? TryRead(string target)
    {
        try
        {
            return SecretStore.Read(target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryWrite(string target, string value)
    {
        try
        {
            SecretStore.Write(target, value);
        }
        catch (Exception)
        {
            // Not kept: the server will be started without it.
        }
    }

    private static void TryDelete(string target)
    {
        try
        {
            SecretStore.Delete(target);
        }
        catch (Exception)
        {
            // Nothing to remove.
        }
    }
}
