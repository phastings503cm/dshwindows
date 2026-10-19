using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vision.Core;

// MARK: - Whose settings a Claude Code turn loads
//
// Headless `claude -p` shows no workspace-trust question and no per-server approval: started in a folder as it is, it runs
// the hooks, `env` and `apiKeyHelper` of the folder's own .claude/settings(.local).json and starts every server in its
// .mcp.json — so a cloned repository could run code on this PC the moment a chat sent a message. Vision therefore loads
// only the person's own settings (`--setting-sources user`: their ~/.claude settings and user-scope MCP servers), unless
// the person has trusted the folder in Claude Code itself — its trust question, recorded as
// `projects[<folder>].hasTrustDialogAccepted` in ~/.claude.json (or %CLAUDE_CONFIG_DIR%\.claude.json), for the folder or a
// folder above it (never inherited from the home folder or a drive's root). Only the `projects` entries are read — the
// file's other fields are never looked at, and the CLI's sign-in isn't in it. Inside WSL the record lives in the Linux
// home, so a WSL route always loads the person's own settings only. The same rule as Vision for macOS (ClaudeCodeTrust.swift).

/// <summary>What Claude Code's own config says about one folder.</summary>
public sealed record ClaudeCodeProjectRecord(bool Trusted, string? LastSessionId);

/// <summary>How much of a folder's own Claude Code settings a turn there loads.</summary>
public enum ClaudeCodeFolderTrust
{
    /// <summary>Trusted in Claude Code: the folder's own settings load, as they would in a terminal.</summary>
    Trusted,
    /// <summary>Not trusted: only the person's own settings load (<c>--setting-sources user</c>).</summary>
    UserOnly,
    /// <summary>Not trusted, the folder has settings of its own, and this CLI can't be told to leave them out.</summary>
    CantSkip,
}

/// <summary>What a turn in a folder loads; <paramref name="Skipped"/> = the folder's own files left out (empty: nothing to say).</summary>
public sealed record ClaudeCodeFolderSettings(ClaudeCodeFolderTrust Trust, IReadOnlyList<string> Skipped)
{
    /// <summary>Pass the folder's own settings (no <c>--setting-sources</c>).</summary>
    public bool LoadsProjectSettings => Trust == ClaudeCodeFolderTrust.Trusted;
}

public static partial class ClaudeCodeTrust
{
    /// <summary>The folder's own files a turn would otherwise load, as the notice names them.</summary>
    public static readonly IReadOnlyList<string> ProjectFiles =
        ["CLAUDE.md", "CLAUDE.local.md", ".claude/CLAUDE.md", ".claude/settings.json", ".claude/settings.local.json", ".mcp.json"];

    /// <summary>Claude Code's own config file: %CLAUDE_CONFIG_DIR%\.claude.json, else .claude.json in the home folder — as the
    /// CLI Vision starts would see it (its environment, <see cref="ClaudeCodeEnvironment"/>).</summary>
    public static string ConfigFile(string? configDir = null, string? home = null)
    {
        configDir ??= ConfigDirForCli();
        if (configDir is { Length: > 0 }) return Path.Combine(configDir, ".claude.json");
        return Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    }

    private static string? ConfigDirForCli()
    {
        if (ClaudeCodeEnvironment.OverridesForTesting is { } overrides && overrides.TryGetValue("CLAUDE_CONFIG_DIR", out var overridden))
            return overridden;
        return Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    }

    /// <summary>The <c>projects</c> entries of Claude Code's config, keyed by folder (separators as '/', no trailing one).
    /// Empty when the file is missing or isn't JSON. Nothing else in the file is read.</summary>
    public static IReadOnlyDictionary<string, ClaudeCodeProjectRecord> ReadProjects(string? configFile = null, bool? ignoreCase = null)
    {
        try
        {
            var path = configFile ?? ConfigFile();
            return File.Exists(path) ? ParseProjects(File.ReadAllText(path), ignoreCase) : Empty(ignoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Empty(ignoreCase);
        }
    }

    private static Dictionary<string, ClaudeCodeProjectRecord> Empty(bool? ignoreCase) =>
        new(ignoreCase ?? OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary><see cref="ReadProjects"/> on the file's text.</summary>
    public static IReadOnlyDictionary<string, ClaudeCodeProjectRecord> ParseProjects(string json, bool? ignoreCase = null)
    {
        var projects = Empty(ignoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("projects", out var list)
                || list.ValueKind != JsonValueKind.Object) return projects;
            foreach (var project in list.EnumerateObject())
            {
                if (project.Value.ValueKind != JsonValueKind.Object) continue;
                var trusted = project.Value.TryGetProperty("hasTrustDialogAccepted", out var t) && t.ValueKind == JsonValueKind.True;
                var last = project.Value.TryGetProperty("lastSessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                projects[Key(project.Name)] = new ClaudeCodeProjectRecord(trusted, last);
            }
        }
        catch (JsonException)
        {
            // Not JSON (being written right now, or hand-edited): nothing is trusted.
        }
        return projects;
    }

    /// <summary>A folder as Claude Code's config keys it: '/' separators, no trailing one (except a root).</summary>
    public static string Key(string path)
    {
        var key = path.Replace('\\', '/');
        while (key.Length > 1 && key.EndsWith('/') && !IsRoot(key)) key = key[..^1];
        return key;
    }

    private static bool IsRoot(string key) => key == "/" || DriveRoot().IsMatch(key) || ShareRoot().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z]:/?$")]
    private static partial Regex DriveRoot();

    [GeneratedRegex(@"^//[^/]+/[^/]+/?$")]
    private static partial Regex ShareRoot();

    /// <summary>Whether the person trusted <paramref name="folder"/> in Claude Code: its config says so for the folder, or for a
    /// folder above it other than the home folder and a root. The folder is checked as Vision names it and with its links
    /// resolved (Claude Code keys the real path).</summary>
    public static bool IsTrusted(string folder, IReadOnlyDictionary<string, ClaudeCodeProjectRecord> projects, string? home = null)
    {
        if (projects.Count == 0) return false;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var comparer = projects is Dictionary<string, ClaudeCodeProjectRecord> d ? d.Comparer : StringComparer.Ordinal;
        var homes = new HashSet<string>(comparer) { Key(home), Key(ClaudeCodePaths.Real(home)) };
        foreach (var start in new HashSet<string>(comparer) { Key(folder), Key(ClaudeCodePaths.Real(folder)) })
        {
            if (projects.TryGetValue(start, out var own) && own.Trusted) return true;
            var path = start;
            while (Parent(path) is { } parent && !IsRoot(parent) && !homes.Contains(parent))
            {
                if (projects.TryGetValue(parent, out var above) && above.Trusted) return true;
                path = parent;
            }
        }
        return false;
    }

    /// <summary>The session Claude Code last saved for exactly this folder (it restores that session's running cost when the
    /// same session is resumed there next); null when it has none — or the folder isn't in its config.</summary>
    public static ClaudeCodeProjectRecord? Record(string folder, IReadOnlyDictionary<string, ClaudeCodeProjectRecord> projects) =>
        projects.TryGetValue(Key(folder), out var own) ? own
        : projects.TryGetValue(Key(ClaudeCodePaths.Real(folder)), out var real) ? real : null;

    private static string? Parent(string key)
    {
        var at = key.LastIndexOf('/');
        if (at <= 0) return at == 0 && key.Length > 1 ? "/" : null;
        var parent = key[..at];
        return DriveRoot().IsMatch(parent + "/") && parent.Length == 2 ? parent + "/" : parent;
    }

    /// <summary>The folder's own Claude Code files that exist (in <see cref="ProjectFiles"/> order).</summary>
    public static IReadOnlyList<string> Present(string folder) =>
        ProjectFiles.Where(f => File.Exists(Path.Combine(folder, f.Replace('/', Path.DirectorySeparatorChar)))).ToList();

    /// <summary>What a turn in <paramref name="folder"/> loads. <paramref name="canSkip"/> = the CLI takes --setting-sources.</summary>
    public static ClaudeCodeFolderSettings Settings(string folder, bool trusted, bool canSkip = true)
    {
        if (trusted) return new ClaudeCodeFolderSettings(ClaudeCodeFolderTrust.Trusted, []);
        var found = Present(folder);
        return new ClaudeCodeFolderSettings(canSkip || found.Count == 0 ? ClaudeCodeFolderTrust.UserOnly : ClaudeCodeFolderTrust.CantSkip, found);
    }

    /// <summary>Said once per chat per launch when a folder's own files are left out.</summary>
    public static string SkippedNotice(IReadOnlyList<string> files) =>
        $"Claude Code isn't using this folder's own settings ({string.Join(", ", files)}): you haven't trusted the folder in Claude Code, " +
        "so its hooks and MCP servers stay off. To use them, run  claude  once in this folder in a terminal and accept its trust question.";

    /// <summary>Why a turn can't start on a CLI that can't leave the folder's own settings out.</summary>
    public static string CantSkipMessage(string? version, IReadOnlyList<string> files) =>
        $"This folder has Claude Code settings of its own ({string.Join(", ", files)}), and {(version is { Length: > 0 } v ? $"Claude Code {v}" : "this Claude Code")} " +
        "can't be told to leave them out — their hooks and MCP servers would run without asking. Update it in a terminal (claude update), " +
        "or trust the folder: run  claude  once in it and accept its trust question.";
}

// MARK: - Paths as they really are

public static class ClaudeCodePaths
{
    /// <summary>The path with its symbolic links and junctions resolved (the part that exists; the rest appended), so a link in
    /// the project that points out of it compares as where it really goes. The path itself when it can't be resolved.</summary>
    public static string Real(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
        var root = Path.GetPathRoot(full) ?? "";
        if (root.Length == 0) return full;
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            try
            {
                FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
                if (info is null) return Path.Combine([current, .. parts[i..]]);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) next = target.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Can't look: take it as written.
            }
            current = next;
        }
        return current;
    }

    /// <summary><paramref name="path"/> is <paramref name="root"/> or inside it (both full paths; case-insensitive on Windows).</summary>
    public static bool Contains(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (r.Length == 0) return true; // "/"
        if (string.Equals(r, p, comparison)) return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, comparison) || p.StartsWith(r + Path.AltDirectorySeparatorChar, comparison);
    }
}
