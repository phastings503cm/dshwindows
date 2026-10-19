using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - MCP servers: what is configured
//
// An MCP server (Model Context Protocol) offers tools — and sometimes resources — to the model. Vision talks to two kinds:
// a program it starts and speaks to over stdin/stdout (stdio), and a server at a URL (Streamable HTTP, falling back to the
// older SSE transport for a server that only speaks that). Each server's tools are offered to the model as
// mcp__<server>__<tool>, behind the usual permission prompt.
//
// The list lives in mcp-servers.json in the data folder — the same file as Vision for macOS: {version, servers: [{id, name,
// transport, command, args, cwd, url, envNames, headerNames, enabled, trust, source, note, disabledTools}]}, plus Windows'
// own "project" (a server from a project's file applies only there) and "sourceFile". The values of a server's environment
// variables and HTTP headers live in Credential Manager (Vision/mcp/<id>/env/<NAME>, Vision/mcp/<id>/header/<Name>), never
// in the file.

[JsonConverter(typeof(JsonStringEnumConverter<McpTransportKind>))]
public enum McpTransportKind
{
    /// <summary>A program Vision starts, spoken to over stdin/stdout.</summary>
    [JsonStringEnumMemberName("stdio")] Stdio,
    /// <summary>A server at a URL (Streamable HTTP; the older SSE transport when that is all it speaks).</summary>
    [JsonStringEnumMemberName("http")] Http,
}

/// <summary>When a server's tools may run without asking.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<McpTrust>))]
public enum McpTrust
{
    /// <summary>Every call asks first (the default).</summary>
    [JsonStringEnumMemberName("ask")] Ask,
    /// <summary>Tools the server marks read-only run without asking; the others ask.</summary>
    [JsonStringEnumMemberName("readOnly")] ReadOnly,
    /// <summary>Every tool runs without asking.</summary>
    [JsonStringEnumMemberName("always")] Always,
}

public static class McpTrusts
{
    public static string Label(this McpTrust trust) => trust switch
    {
        McpTrust.ReadOnly => "Read-only tools without asking",
        McpTrust.Always => "All tools without asking",
        _ => "Ask before every tool call",
    };
}

/// <summary>One configured MCP server.</summary>
public sealed record McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>The name the model sees in front of its tools (mcp__&lt;name&gt;__&lt;tool&gt;).</summary>
    public string Name { get; set; } = "";
    public McpTransportKind Transport { get; set; } = McpTransportKind.Stdio;
    /// <summary>stdio: the program and its arguments, and the folder it starts in (null = the project's, else your user folder).</summary>
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public string? Cwd { get; set; }
    /// <summary>http: the server's address.</summary>
    public string? Url { get; set; }
    /// <summary>The names of its environment variables (stdio) and HTTP headers (http); their values are in Credential Manager.</summary>
    public List<string> EnvNames { get; set; } = [];
    public List<string> HeaderNames { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public McpTrust Trust { get; set; } = McpTrust.Ask;
    /// <summary>Where it was imported from ("Claude Code", "Cursor", …); null = added in Vision.</summary>
    public string? Source { get; set; }
    /// <summary>A word about it the settings show (an SSE server imported as Streamable HTTP, …).</summary>
    public string? Note { get; set; }
    /// <summary>Tools it offers that aren't given to the model.</summary>
    public List<string> DisabledTools { get; set; } = [];
    /// <summary>The project folder it belongs to (from a project's own file); null = every chat. Windows only.</summary>
    public string? Project { get; set; }
    /// <summary>The file it was imported from. Windows only.</summary>
    public string? SourceFile { get; set; }

    /// <summary>One line about where it runs: "npx -y @modelcontextprotocol/server-github" or the URL.</summary>
    [JsonIgnore]
    public string Summary => Transport == McpTransportKind.Stdio
        ? string.Join(" ", new[] { Command ?? "" }.Concat(Args).Where(s => s.Length > 0))
        : Url ?? "";

    /// <summary>What is missing for it to start (null = nothing).</summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (McpNames.Sanitize(Name).Length == 0) return "It needs a name.";
            if (Transport == McpTransportKind.Stdio) return string.IsNullOrWhiteSpace(Command) ? "It needs a program to run." : null;
            return Uri.TryCreate(Url ?? "", UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && url.Host.Length > 0
                ? null : "It needs an http(s) address.";
        }
    }

    public McpServerConfig Copy() => this with { Args = [.. Args], EnvNames = [.. EnvNames], HeaderNames = [.. HeaderNames], DisabledTools = [.. DisabledTools] };

    public static string EnvTarget(string id, string name) => $"{SecretStore.Prefix}mcp/{id}/env/{name}";
    public static string HeaderTarget(string id, string name) => $"{SecretStore.Prefix}mcp/{id}/header/{name}";

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

/// <summary>Tool names as the model sees them.</summary>
public static class McpNames
{
    /// <summary>Tool names may hold letters, digits, '_' and '-', up to 64 characters (OpenAI, Bedrock).</summary>
    public const int MaxLength = 64;

    /// <summary>Other characters become '_'; underscores at either end go.</summary>
    public static string Sanitize(string raw) =>
        new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray()).Trim('_');

    /// <summary>mcp__&lt;server&gt;__&lt;tool&gt;, shortened (with a stable tag) when longer than the limit.</summary>
    public static string ToolName(string server, string tool)
    {
        var name = $"mcp__{Sanitize(server)}__{Sanitize(tool)}";
        if (name.Length <= MaxLength) return name;
        var tag = CheckpointStore.Hash(System.Text.Encoding.UTF8.GetBytes(name))[..6];
        return name[..(MaxLength - 7)] + "_" + tag;
    }

    /// <summary>A name that doesn't clash with one already there ("github", "github-2", …), compared without regard to case.</summary>
    public static string Unique(string name, IEnumerable<string> taken)
    {
        var baseName = Sanitize(name) is { Length: > 0 } clean ? clean : "server";
        var lowered = taken.Select(t => t.ToLowerInvariant()).ToHashSet();
        var candidate = baseName;
        for (var n = 2; lowered.Contains(candidate.ToLowerInvariant()); n++) candidate = $"{baseName}-{n}";
        return candidate;
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
            lock (_lock) _servers = stored?.Servers.Where(s => s.Id.Length > 0 && s.Name.Length > 0).ToList() ?? [];
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

    /// <summary>A name no other server has (see <see cref="McpNames.Unique"/>).</summary>
    public string UniqueName(string wanted, string? exceptId = null)
    {
        lock (_lock) return McpNames.Unique(wanted, _servers.Where(s => s.Id != exceptId).Select(s => McpNames.Sanitize(s.Name)));
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
            foreach (var name in old?.EnvNames.Except(env.Keys) ?? []) TryDelete(McpServerConfig.EnvTarget(copy.Id, name));
            foreach (var (name, value) in env) TryWrite(McpServerConfig.EnvTarget(copy.Id, name), value);
            copy.EnvNames = env.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
        if (headers is not null)
        {
            foreach (var name in old?.HeaderNames.Except(headers.Keys) ?? []) TryDelete(McpServerConfig.HeaderTarget(copy.Id, name));
            foreach (var (name, value) in headers) TryWrite(McpServerConfig.HeaderTarget(copy.Id, name), value);
            copy.HeaderNames = headers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
        lock (_lock)
        {
            var index = _servers.FindIndex(s => s.Id == copy.Id);
            if (index >= 0) _servers[index] = copy; else _servers.Add(copy);
        }
        Save();
    }

    public void SetEnabled(string id, bool enabled) => Update(id, s => s.Enabled = enabled);
    public void SetTrust(string id, McpTrust trust) => Update(id, s => s.Trust = trust);

    /// <summary>Give one of a server's tools to the model, or keep it back.</summary>
    public void SetToolEnabled(string id, string tool, bool enabled) => Update(id, s =>
    {
        s.DisabledTools.Remove(tool);
        if (!enabled) s.DisabledTools.Add(tool);
    });

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
        foreach (var name in old.EnvNames) TryDelete(McpServerConfig.EnvTarget(id, name));
        foreach (var name in old.HeaderNames) TryDelete(McpServerConfig.HeaderTarget(id, name));
        Save();
    }

    /// <summary>The server's environment variables with their values (read from Credential Manager; a missing one is empty).</summary>
    public static Dictionary<string, string> EnvOf(McpServerConfig server) =>
        server.EnvNames.ToDictionary(k => k, k => TryRead(McpServerConfig.EnvTarget(server.Id, k)) ?? "", StringComparer.Ordinal);

    public static Dictionary<string, string> HeadersOf(McpServerConfig server) =>
        server.HeaderNames.ToDictionary(k => k, k => TryRead(McpServerConfig.HeaderTarget(server.Id, k)) ?? "", StringComparer.OrdinalIgnoreCase);

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
