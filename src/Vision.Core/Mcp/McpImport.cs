using System.Text.Json;

namespace Vision.Core;

// MARK: - Bringing in the MCP servers other apps already have
//
// The servers configured for Claude Code (~/.claude.json: its own "mcpServers" and each project's under
// "projects"/<folder>; a project's .mcp.json), Claude Desktop (claude_desktop_config.json: %APPDATA%\Claude on Windows,
// ~/Library/Application Support/Claude on a Mac) and Cursor (~/.cursor/mcp.json, a project's .cursor/mcp.json). Each file
// has the same shape:
//
//   { "mcpServers": { "github": { "command": "npx", "args": ["-y", "…"], "env": { "GITHUB_TOKEN": "…" } },
//                     "docs":   { "type": "http", "url": "https://…/mcp", "headers": { "Authorization": "Bearer …" } } } }
//
// Nothing is started while looking. Importing copies a server into Vision's list; its environment and header values go to
// Credential Manager. A value like ${GITHUB_TOKEN} is kept as written and filled in from the environment when the server
// starts, as Claude Code does. The labels and rules are Vision for macOS's (MCPImport.swift); Windows also looks at the
// recent projects' files, and a server from a project's own file applies only in that project.

/// <summary>One server another app has configured, ready to import.</summary>
/// <param name="Caveat">Why it may not work as configured there (an SSE server); null = fine.</param>
public sealed record McpCandidate(McpServerConfig Server, IReadOnlyDictionary<string, string> Env, IReadOnlyDictionary<string, string> Headers,
                                  string Origin, string? Caveat = null)
{
    /// <summary>What makes two servers the same one: how it starts or where it is.</summary>
    public string Fingerprint => $"{Server.Transport}|{Server.Summary}".ToLowerInvariant();

    /// <summary>Whether Vision already has it (it starts the same way, or is at the same address).</summary>
    public bool IsKnown(IEnumerable<McpServerConfig> existing) => existing.Any(s => $"{s.Transport}|{s.Summary}".ToLowerInvariant() == Fingerprint);
}

public static class McpImport
{
    public const string ClaudeCode = "Claude Code";
    public const string ClaudeDesktop = "Claude Desktop";
    public const string Cursor = "Cursor";
    public const string SseCaveat =
        "It uses the older SSE transport; Vision speaks Streamable HTTP, which many such servers also offer at the same address (or at /mcp).";

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

    /// <summary>"Claude Code (this project)" for the open project, "Claude Code (app)" for another.</summary>
    private static string ForProject(string app, string project, string? current) =>
        current is not null && SameFolder(project, current) ? $"{app} (this project)" : $"{app} ({Path.GetFileName(project.TrimEnd('\\', '/'))})";

    /// <summary>Every server Claude Code, Claude Desktop and Cursor have, with those of the projects' own files (the open one,
    /// <paramref name="current"/>, and <paramref name="projects"/>). Nothing is started or changed.</summary>
    public static IReadOnlyList<McpCandidate> Scan(ExternalLocations locations, string? current, IEnumerable<string>? projects = null)
    {
        var folders = new List<string>();
        foreach (var folder in (current is null ? [] : new[] { current }).Concat(projects ?? []))
            if (!string.IsNullOrWhiteSpace(folder) && !folders.Any(f => SameFolder(f, folder))) folders.Add(folder);
        var output = new List<McpCandidate>();
        if (ReadJson(locations.ClaudeGlobalConfig) is { } claude)
        {
            output.AddRange(Servers(claude, ClaudeCode, locations.ClaudeGlobalConfig, project: null));
            // Claude Code keeps a project's own servers inside ~/.claude.json, under its folder.
            if (claude.TryGetProperty("projects", out var perProject) && perProject.ValueKind == JsonValueKind.Object)
                foreach (var entry in perProject.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Object && folders.FirstOrDefault(f => SameFolder(f, entry.Name)) is { } folder)
                        output.AddRange(Servers(entry.Value, ForProject(ClaudeCode, folder, current), locations.ClaudeGlobalConfig, folder));
        }
        var desktop = ClaudeDesktopConfig(locations);
        if (ReadJson(desktop) is { } desktopConfig) output.AddRange(Servers(desktopConfig, ClaudeDesktop, desktop, project: null));
        var cursor = Path.Combine(locations.CursorHome, "mcp.json");
        if (ReadJson(cursor) is { } cursorConfig) output.AddRange(Servers(cursorConfig, Cursor, cursor, project: null));
        foreach (var folder in folders)
        {
            var shared = Path.Combine(folder, ".mcp.json");
            if (ReadJson(shared) is { } sharedConfig) output.AddRange(Servers(sharedConfig, ForProject(ClaudeCode, folder, current), shared, folder));
            var cursorProject = Path.Combine(folder, ".cursor", "mcp.json");
            if (ReadJson(cursorProject) is { } cursorProjectConfig) output.AddRange(Servers(cursorProjectConfig, ForProject(Cursor, folder, current), cursorProject, folder));
        }
        return output;
    }

    /// <summary>The servers under "mcpServers" in <paramref name="root"/>, by name.</summary>
    public static IEnumerable<McpCandidate> Servers(JsonElement root, string source, string file, string? project)
    {
        if (!root.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) yield break;
        foreach (var entry in servers.EnumerateObject().OrderBy(e => e.Name, StringComparer.Ordinal))
            if (Parse(entry.Name, entry.Value, source, file, project) is { } candidate) yield return candidate;
    }

    /// <summary>One "mcpServers" entry; null when it names neither a command nor a URL.</summary>
    public static McpCandidate? Parse(string name, JsonElement entry, string source, string file, string? project)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var type = (Str(entry, "type") ?? Str(entry, "transport"))?.ToLowerInvariant();
        var url = Str(entry, "url") ?? Str(entry, "serverUrl");
        var command = Str(entry, "command");
        var enabled = !(entry.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
                      && !(entry.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.False);
        McpServerConfig server;
        Dictionary<string, string> env = new(StringComparer.Ordinal), headers = new(StringComparer.Ordinal);
        string? caveat = null;
        if (url is not null && type != "stdio" && (command is null || type is not null))
        {
            if (type == "sse") caveat = SseCaveat;
            headers = Map(entry, "headers");
            server = new McpServerConfig
            {
                Name = name, Transport = McpTransportKind.Http, Url = url, HeaderNames = headers.Keys.Order(StringComparer.Ordinal).ToList(),
                Enabled = enabled, Source = source, Note = caveat, Project = project, SourceFile = file,
            };
        }
        else if (!string.IsNullOrEmpty(command))
        {
            env = Map(entry, "env");
            server = new McpServerConfig
            {
                Name = name, Transport = McpTransportKind.Stdio, Command = command, Args = Strings(entry, "args"), Cwd = Str(entry, "cwd"),
                EnvNames = env.Keys.Order(StringComparer.Ordinal).ToList(), Enabled = enabled, Source = source, Project = project, SourceFile = file,
            };
        }
        else
        {
            return null;
        }
        return new McpCandidate(server, env, headers, file, caveat);
    }

    /// <summary>Import <paramref name="candidates"/>: a server Vision already has (same start or address) is updated in place —
    /// its secrets refreshed, its switch and trust kept; a new one gets a name of its own ("github", "github-2"). Secrets go to
    /// Credential Manager. Returns how many were added and how many updated.</summary>
    public static (int Added, int Updated) Import(McpServerStore store, IEnumerable<McpCandidate> candidates)
    {
        int added = 0, updated = 0;
        foreach (var candidate in candidates)
        {
            var server = candidate.Server.Copy();
            var existing = store.Servers.FirstOrDefault(s => candidate.IsKnown([s]));
            if (existing is not null)
            {
                server = server with
                {
                    Id = existing.Id, Name = existing.Name, Enabled = existing.Enabled, Trust = existing.Trust, Project = existing.Project,
                    DisabledTools = [.. existing.DisabledTools],
                };
                updated++;
            }
            else
            {
                server = server with { Id = Guid.NewGuid().ToString(), Name = store.UniqueName(server.Name) };
                added++;
            }
            store.Save(server, new Dictionary<string, string>(candidate.Env), new Dictionary<string, string>(candidate.Headers));
        }
        return (added, updated);
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static JsonElement? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
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
        {
            switch (item.Value.ValueKind)
            {
                case JsonValueKind.String: output[item.Name] = item.Value.GetString() ?? ""; break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False: output[item.Name] = item.Value.GetRawText(); break;
            }
        }
        return output;
    }
}
