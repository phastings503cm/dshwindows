using System.Text.Json;

namespace Vision.Core;

// MARK: - Bringing in the MCP servers other tools already have
//
// Claude Code lists its servers in ~/.claude.json (user-wide "mcpServers", and per project under "projects"/<folder>), and
// each project may have a .mcp.json; Claude Desktop in claude_desktop_config.json (%APPDATA%\Claude on Windows,
// ~/Library/Application Support/Claude on a Mac); Cursor in ~/.cursor/mcp.json and a project's .cursor/mcp.json. All of
// them share one shape: "mcpServers": { "<name>": { "command", "args", "env" } or { "type", "url", "headers" } }.
// Importing copies each server into Vision's list and moves its env and header values into Credential Manager.

/// <summary>One server another tool has configured, ready to import.</summary>
public sealed record McpCandidate(McpServerConfig Server, IReadOnlyDictionary<string, string> Env, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>"Claude Code · user", "Cursor · C:\work\app"…</summary>
    public string Where => Server.Project is { } project ? $"{Server.Source} · {project}" : $"{Server.Source} · every chat";
}

public static class McpImport
{
    public const string ClaudeCode = "Claude Code";
    public const string ClaudeDesktop = "Claude Desktop";
    public const string Cursor = "Cursor";
    public const string ProjectFile = "Project .mcp.json";

    /// <summary>Claude Desktop's settings file on this platform.</summary>
    public static string ClaudeDesktopConfig(ExternalLocations locations)
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(locations.Home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        var appData = locations == ExternalLocations.Standard
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(locations.Home, "AppData", "Roaming");
        return Path.Combine(appData, "Claude", "claude_desktop_config.json");
    }

    /// <summary>Every server Claude Code, Claude Desktop and Cursor have, plus those of <paramref name="projects"/>' own
    /// .mcp.json and .cursor/mcp.json. Nothing is changed.</summary>
    public static IReadOnlyList<McpCandidate> Scan(ExternalLocations locations, IEnumerable<string> projects)
    {
        var output = new List<McpCandidate>();
        // Claude Code: user-wide, then per project ("local" scope).
        if (ReadJson(locations.ClaudeGlobalConfig) is { } claude)
        {
            output.AddRange(Servers(claude, ClaudeCode, locations.ClaudeGlobalConfig, project: null));
            if (claude.TryGetProperty("projects", out var perProject) && perProject.ValueKind == JsonValueKind.Object)
                foreach (var project in perProject.EnumerateObject())
                    if (project.Value.ValueKind == JsonValueKind.Object)
                        output.AddRange(Servers(project.Value, ClaudeCode, locations.ClaudeGlobalConfig, project.Name));
        }
        var desktop = ClaudeDesktopConfig(locations);
        if (ReadJson(desktop) is { } desktopConfig) output.AddRange(Servers(desktopConfig, ClaudeDesktop, desktop, project: null));
        var cursor = Path.Combine(locations.CursorHome, "mcp.json");
        if (ReadJson(cursor) is { } cursorConfig) output.AddRange(Servers(cursorConfig, Cursor, cursor, project: null));
        foreach (var project in projects.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(TurnRecorder.PathComparer))
        {
            var shared = Path.Combine(project, ".mcp.json");
            if (ReadJson(shared) is { } sharedConfig) output.AddRange(Servers(sharedConfig, ProjectFile, shared, project));
            var cursorProject = Path.Combine(project, ".cursor", "mcp.json");
            if (ReadJson(cursorProject) is { } cursorProjectConfig) output.AddRange(Servers(cursorProjectConfig, Cursor, cursorProject, project));
        }
        return output;
    }

    /// <summary>The servers under "mcpServers" in <paramref name="root"/>.</summary>
    public static IEnumerable<McpCandidate> Servers(JsonElement root, string source, string file, string? project)
    {
        if (!root.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) yield break;
        foreach (var entry in servers.EnumerateObject())
            if (Parse(entry.Name, entry.Value, source, file, project) is { } candidate) yield return candidate;
    }

    /// <summary>One "mcpServers" entry; null when it names neither a command nor a URL.</summary>
    public static McpCandidate? Parse(string name, JsonElement entry, string source, string file, string? project)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var command = Str(entry, "command");
        var url = Str(entry, "url") ?? Str(entry, "serverUrl");
        var type = (Str(entry, "type") ?? Str(entry, "transport") ?? "").ToLowerInvariant();
        McpTransportKind transport;
        if (type is "http" or "streamable-http" or "streamablehttp") transport = McpTransportKind.Http;
        else if (type == "sse") transport = McpTransportKind.Sse;
        else if (!string.IsNullOrWhiteSpace(command)) transport = McpTransportKind.Stdio;
        else if (!string.IsNullOrWhiteSpace(url)) transport = McpTransportKind.Http;
        else return null;
        if (transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(command)) return null;
        if (transport != McpTransportKind.Stdio && string.IsNullOrWhiteSpace(url)) return null;
        var server = new McpServerConfig
        {
            Id = McpServerConfig.IdFrom(name),
            Name = name,
            Transport = transport,
            Command = transport == McpTransportKind.Stdio ? command : null,
            Args = transport == McpTransportKind.Stdio ? Strings(entry, "args") : [],
            Cwd = Str(entry, "cwd"),
            Url = transport == McpTransportKind.Stdio ? null : url,
            // A server another tool has switched off comes in switched off.
            Enabled = !(entry.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True),
            Project = project,
            Source = source,
            SourceFile = file,
        };
        return new McpCandidate(server, Map(entry, "env"), Map(entry, "headers"));
    }

    /// <summary>Import <paramref name="candidates"/>: each gets an id of its own (a second "github" becomes "github-2"), its
    /// secrets go to Credential Manager. A server already imported from the same file under the same name is replaced,
    /// not duplicated. Returns how many were added and how many replaced.</summary>
    public static (int Added, int Replaced) Import(McpServerStore store, IEnumerable<McpCandidate> candidates)
    {
        int added = 0, replaced = 0;
        foreach (var candidate in candidates)
        {
            var server = candidate.Server.Copy();
            var existing = store.Servers.FirstOrDefault(s => s.Name == server.Name && s.SourceFile == server.SourceFile
                                                              && string.Equals(s.Project, server.Project, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                server.Id = existing.Id;
                server.Enabled = existing.Enabled;
                server.AskBeforeUse = existing.AskBeforeUse;
                replaced++;
            }
            else
            {
                server.Id = store.UniqueId(server.Name);
                added++;
            }
            store.Save(server, new Dictionary<string, string>(candidate.Env), new Dictionary<string, string>(candidate.Headers));
        }
        return (added, replaced);
    }

    private static JsonElement? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // Claude Code's state file can be large; it is read once, and only its mcpServers are kept.
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement entry, string key) =>
        entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(JsonElement entry, string key) =>
        entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()).ToList()
            : [];

    private static Dictionary<string, string> Map(JsonElement entry, string key)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!entry.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return output;
        foreach (var item in value.EnumerateObject())
            output[item.Name] = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() ?? "" : item.Value.GetRawText();
        return output;
    }
}
