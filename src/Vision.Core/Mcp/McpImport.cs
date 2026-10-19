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
    /// <summary>Read from a file inside a project folder (.mcp.json, .cursor/mcp.json): the repository chose what it runs.</summary>
    public bool FromRepository { get; init; }
    /// <summary>What Claude Code recorded about a project .mcp.json server: "approved", "declined", or null (never asked).</summary>
    public string? Approval { get; init; }
    /// <summary>${NAME}s it uses that Vision's environment doesn't have.</summary>
    public IReadOnlyList<string> Needs { get; init; } = [];
    /// <summary>Its command line or address seems to hold a key (kept as written, not in Credential Manager).</summary>
    public bool SecretInPlainText { get; init; }

    /// <summary>Checked when the import window opens: your own apps' servers, and a repository's only when you approved it in
    /// Claude Code.</summary>
    public bool Preselected => !FromRepository || Approval == "approved";

    /// <summary>What the import window says under it (the SSE caveat, a repository's server, missing variables, keys in the
    /// command line), in that order — the Mac's words, with Windows' names for its places.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var output = new List<string>();
            if (Caveat is { } caveat) output.Add(caveat);
            if (FromRepository)
                output.Add(Approval switch
                {
                    "declined" => McpImport.DeclinedWarning,
                    "approved" => McpImport.ApprovedWarning,
                    _ => McpImport.RepositoryWarning,
                });
            if (Needs.Count > 0) output.Add(McpImport.NeedsWarning(Needs, Server.Transport));
            if (SecretInPlainText) output.Add(McpImport.PlainTextSecretWarning);
            return output;
        }
    }

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
    public const string RepositoryWarning =
        "From the project's own files: whoever wrote the repository chose what it runs. It is imported switched off — read its command first, then turn it on.";
    public const string ApprovedWarning =
        "From the project's own files (you approved it in Claude Code). It is imported switched off — check its command, then turn it on.";
    public const string DeclinedWarning =
        "From the project's own files, and you declined it in Claude Code. Import it only if you've changed your mind; it is imported switched off.";
    public const string PlainTextSecretWarning =
        "Its command line or address seems to hold a key or token. Vision keeps those as written in mcp-servers.json, not in Credential Manager — " +
        "after importing, move the key into an environment variable (or a header) in Settings › MCP Servers.";

    /// <summary>"Needs GITHUB_TOKEN: …" — where a ${NAME} Vision can't see will come from.</summary>
    public static string NeedsWarning(IReadOnlyList<string> names, McpTransportKind transport)
    {
        var list = string.Join(", ", names);
        var one = names.Count == 1;
        return transport == McpTransportKind.Stdio
            ? $"Needs {list}: Vision doesn't have {(one ? "it" : "them")}, so {(one ? "it comes" : "they come")} from your Windows user environment " +
              "variables when the server starts — or put the value in the server's environment in Settings › MCP Servers."
            : $"Needs {list}: Vision doesn't have {(one ? "it" : "them")} — put the value in the server's header in Settings › MCP Servers.";
    }

    /// <summary>What the import window says when it is done (the Mac's words).</summary>
    public static string Summary(IReadOnlyList<McpServerConfig> added)
    {
        if (added.Count == 0) return "Nothing new to import.";
        var on = added.Where(s => s.Enabled).Select(s => s.Name).ToList();
        var off = added.Where(s => !s.Enabled).Select(s => s.Name).ToList();
        var parts = new List<string>();
        if (on.Count > 0) parts.Add($"Imported {string.Join(", ", on)} — connecting now.");
        if (off.Count > 0)
            parts.Add($"{(on.Count == 0 ? "Imported " : "")}{string.Join(", ", off)} switched off — turn {(off.Count == 1 ? "it" : "them")} on in the list " +
                      $"once you've checked what {(off.Count == 1 ? "it runs" : "they run")}.");
        return string.Join(" ", parts);
    }

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
        var claudeConfig = ReadJson(locations.ClaudeGlobalConfig);
        if (claudeConfig is { } claude)
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
            // A project's own files come with the repository: what they run was chosen by whoever wrote it.
            var (approved, declined) = Approvals(claudeConfig, folder);
            var shared = Path.Combine(folder, ".mcp.json");
            if (ReadJson(shared) is { } sharedConfig)
                output.AddRange(Servers(sharedConfig, ForProject(ClaudeCode, folder, current), shared, folder).Select(c => c with
                {
                    FromRepository = true,
                    Approval = declined.Contains(c.Server.Name) ? "declined" : approved.Contains(c.Server.Name) ? "approved" : null,
                }));
            var cursorProject = Path.Combine(folder, ".cursor", "mcp.json");
            if (ReadJson(cursorProject) is { } cursorProjectConfig)
                output.AddRange(Servers(cursorProjectConfig, ForProject(Cursor, folder, current), cursorProject, folder).Select(c => c with { FromRepository = true }));
        }
        return output.Select(c => c with { Needs = MissingVariables(c, McpVariables.Lookup), SecretInPlainText = HoldsSecretInPlainText(c.Server) }).ToList();
    }

    /// <summary>Which of a project's .mcp.json servers you approved and declined in Claude Code (~/.claude.json,
    /// projects[&lt;folder&gt;].enabledMcpjsonServers / disabledMcpjsonServers).</summary>
    private static (HashSet<string> Approved, HashSet<string> Declined) Approvals(JsonElement? claude, string folder)
    {
        var approved = new HashSet<string>(StringComparer.Ordinal);
        var declined = new HashSet<string>(StringComparer.Ordinal);
        if (claude is not { } root || !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object) return (approved, declined);
        foreach (var entry in projects.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object || !SameFolder(entry.Name, folder)) continue;
            approved.UnionWith(Strings(entry.Value, "enabledMcpjsonServers"));
            declined.UnionWith(Strings(entry.Value, "disabledMcpjsonServers"));
        }
        return (approved, declined);
    }

    // MARK: Placeholders and keys (the Mac's rules)

    /// <summary>The ${NAME} / ${NAME:-default} references in a text, in order.</summary>
    public static IReadOnlyList<(string Name, bool HasDefault)> Placeholders(string text)
    {
        var output = new List<(string, bool)>();
        var at = 0;
        while ((at = text.IndexOf("${", at, StringComparison.Ordinal)) >= 0)
        {
            var close = text.IndexOf('}', at + 2);
            if (close < 0) break;
            var body = text[(at + 2)..close];
            var dash = body.IndexOf(":-", StringComparison.Ordinal);
            output.Add(dash >= 0 ? (body[..dash], true) : (body, false));
            at = close + 1;
        }
        return output;
    }

    /// <summary>The names a candidate refers to (without a default) that <paramref name="lookup"/> lacks or has empty.</summary>
    public static IReadOnlyList<string> MissingVariables(McpCandidate candidate, Func<string, string?> lookup)
    {
        var server = candidate.Server;
        var texts = new[] { server.Command, server.Url, server.Cwd }.OfType<string>().Concat(server.Args)
            .Concat(candidate.Env.Values.Order(StringComparer.Ordinal)).Concat(candidate.Headers.Values.Order(StringComparer.Ordinal));
        var output = new List<string>();
        foreach (var text in texts)
            foreach (var (name, hasDefault) in Placeholders(text))
                if (!hasDefault && string.IsNullOrEmpty(lookup(name)) && !output.Contains(name)) output.Add(name);
        return output;
    }

    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "key", "auth", "authorization", "token", "secret", "password", "passwd", "apikey", "credential", "credentials", "pat", "bearer", "sig", "signature",
    };
    private static readonly string[] SecretInfixes = ["token", "secret", "password", "apikey"];
    private static readonly HashSet<string> NotSecretWords = new(StringComparer.Ordinal) { "file", "path", "dir", "env", "name", "id" };
    private static readonly string[] SecretPrefixes =
        ["ghp_", "gho_", "ghs_", "ghu_", "github_pat_", "glpat-", "sk-", "sk_live_", "rk_live_", "xoxb-", "xoxp-", "xapp-", "AKIA", "ASIA", "AIza", "ya29.", "hf_", "npm_"];

    /// <summary>Whether a server's command line or address seems to hold a key: an option named like one (--api-key X, --token=X),
    /// a value shaped like a well-known key, or an address with a key in its query or a long random part in its path. ${NAME}
    /// references don't count — those are filled in at start.</summary>
    public static bool HoldsSecretInPlainText(McpServerConfig server)
    {
        static bool Literal(string value) => value.Length > 0 && !value.Contains("${", StringComparison.Ordinal);
        static bool SecretName(string name)
        {
            var lower = name.ToLowerInvariant().Trim('-');
            var words = lower.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            if (words.Overlaps(NotSecretWords)) return false;
            return words.Overlaps(SecretWords) || SecretInfixes.Any(lower.Contains);
        }
        static bool LooksLikeKey(string value) => Literal(value) && SecretPrefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal) && value.Length >= p.Length + 12);
        static bool Randomish(string part) =>
            part.Length >= 24 && Literal(part) && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && part.Any(char.IsDigit) && part.Any(char.IsLetter);
        if (server.Transport == McpTransportKind.Stdio)
        {
            var args = server.Args;
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (LooksLikeKey(arg)) return true;
                if (!arg.StartsWith('-')) continue;
                var equals = arg.IndexOf('=');
                if (equals >= 0)
                {
                    if (SecretName(arg[..equals]) && Literal(arg[(equals + 1)..])) return true;
                }
                else if (SecretName(arg) && i + 1 < args.Count && !args[i + 1].StartsWith('-') && Literal(args[i + 1]))
                {
                    return true;
                }
            }
            return false;
        }
        if (!Uri.TryCreate(server.Url ?? "", UriKind.Absolute, out var url)) return false;
        if (url.UserInfo.Contains(':') && Literal(url.UserInfo[(url.UserInfo.IndexOf(':') + 1)..])) return true;
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && SecretName(Uri.UnescapeDataString(pair[..equals])) && Literal(Uri.UnescapeDataString(pair[(equals + 1)..]))) return true;
        }
        return url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => Randomish(part) || LooksLikeKey(part));
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
    public static (int Added, int Updated) Import(McpServerStore store, IEnumerable<McpCandidate> candidates) => Import(store, candidates, []);

    /// <summary><see cref="Import(McpServerStore, IEnumerable{McpCandidate})"/>, collecting the servers as they were saved.</summary>
    public static (int Added, int Updated) Import(McpServerStore store, IEnumerable<McpCandidate> candidates, List<McpServerConfig> imported)
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
                // A repository's server comes in switched off: read its command first.
                server = server with { Id = Guid.NewGuid().ToString(), Name = store.UniqueName(server.Name), Enabled = server.Enabled && !candidate.FromRepository };
                added++;
            }
            imported.Add(server);
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
