using System.Text;

namespace Dsh.Core;

// MARK: - Agent types
//
// A subagent is a fresh engine working one task. Its *type* decides what it is told to be and which
// tools it may touch: an explorer reads and reports, a worker edits and builds. The built-in types cover
// the usual division of labour; more come from markdown files — the same shape Claude Code uses
// (frontmatter: name, description, tools, model; the body is the system prompt), so agent folders that
// already exist are read in place.

/// <summary>What a kind of subagent is and may do.</summary>
public sealed record AgentDefinition
{
    /// <summary>The value of the agent tool's <c>agent_type</c>.</summary>
    public string Name { get; init; } = "";
    /// <summary>When to use it; shown to the model in the agent tool's description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Instructions added to the standard subagent prompt.</summary>
    public string Prompt { get; init; } = "";
    /// <summary>The only tools it may use (DSH tool names). Null = everything the parent has except the agent tools.</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    /// <summary>Tools it may not use even though <see cref="Tools"/> would allow them (a file's <c>disallowedTools</c>).</summary>
    public IReadOnlyList<string>? DisallowedTools { get; init; }
    /// <summary>Prefer the model server whose name contains this (e.g. "spark-2"); null = whichever is free.</summary>
    public string? Server { get; init; }
    /// <summary>Its own step budget; null = the default.</summary>
    public int? MaxSteps { get; init; }
    public bool ReadOnly { get; init; }

    /// <summary>It can only read: every tool it has is a read-only one. Judged from the tools, not from the name or a
    /// flag, so a project file that replaces "explore" with one that has a shell is not mistaken for the built-in.</summary>
    public bool IsReadOnly => Tools is { Count: > 0 } tools && tools.All(t => ReadOnlyTools.Contains(t));
    /// <summary>"built-in", "user" or "project".</summary>
    public string Origin { get; init; } = "built-in";
    /// <summary>The file it was read from.</summary>
    public string? Path { get; init; }

    public const string General = "general";

    /// <summary>Tools that only read: nothing here changes a file or runs a command.</summary>
    public static IReadOnlyList<string> ReadOnlyTools { get; } =
        ["read_file", "read_many_files", "glob", "grep", "list_directory", "web_fetch", "memory_search"];

    public static IReadOnlyList<AgentDefinition> BuiltIn { get; } =
    [
        new()
        {
            Name = General,
            Description = "A capable all-round worker with every tool. The default.",
        },
        new()
        {
            Name = "explore",
            Description = "Fast, read-only investigator: finds files, traces usages, answers \"where is X / how does Y work\" with file:line evidence. Never changes anything.",
            Tools = ReadOnlyTools,
            ReadOnly = true,
            MaxSteps = 40,
            Prompt =
                "You are a read-only investigator. Search widely before concluding: try several names, spellings and locations, and " +
                "read the code you find rather than guessing from file names. Report what you found as a short list of facts, each " +
                "with its evidence (`path:line`). Say plainly what you looked for and did not find. You cannot change files.",
        },
        new()
        {
            Name = "plan",
            Description = "Read-only planner: studies the code and returns a concrete step-by-step plan (files to touch, order, risks). Changes nothing.",
            Tools = ReadOnlyTools,
            ReadOnly = true,
            MaxSteps = 40,
            Prompt =
                "You are a planner. Study the relevant code, then return a numbered plan another engineer could follow without " +
                "asking questions: which files change and how, in what order, what could go wrong, and how to check each step. " +
                "Prefer the smallest change that fully solves the task. You cannot change files.",
        },
        new()
        {
            Name = "review",
            Description = "Reviews code or a diff for bugs, security problems and unclear code; reports findings by severity. Reads and runs read-only commands.",
            Tools = [.. ReadOnlyTools, "run_shell_command"],
            ReadOnly = true,
            MaxSteps = 40,
            Prompt =
                "You are a code reviewer. Read the code or diff you are pointed at (git diff / git log are fine; run nothing that " +
                "changes the tree). Report only real problems: a bug with the input that triggers it, a security hole, a broken " +
                "assumption. Rank them (high / medium / low), give `path:line` for each, and suggest the fix. If it is sound, say so.",
        },
        new()
        {
            Name = "worker",
            Description = "Implements one well-specified change: edits files, runs builds and tests, reports what changed. Give it a complete task and the files involved.",
            MaxSteps = 60,
            Prompt =
                "You are an implementer. Do exactly the task you were given: read before you edit, make focused changes, then build " +
                "and run the relevant tests and fix what you broke. Your report lists each file you changed and what changed in it, " +
                "the commands you ran with their results, and anything left undone.",
        },
    ];

    /// <summary>Read one agent file. Null when it has no name or nothing to say.</summary>
    public static AgentDefinition? Parse(string text, string fallbackName, string origin, string? path)
    {
        var doc = SkillDocument.Parse(text);
        var name = Slug(doc["name"] ?? fallbackName);
        if (name.Length == 0) return null;
        var body = doc.Body.Trim();
        var description = (doc["description"] ?? "").Trim();
        if (body.Length == 0 && description.Length == 0) return null;
        var requested = doc.List("tools");
        var tools = ResolveTools(requested);
        var toolsLine = HasTopLevelKey(text, "tools");
        // A list that names only tools DSH doesn't have (Task, NotebookRead, mcp__github__…), or is left blank, must not mean
        // "everything": that would hand a restricted agent the shell and every write. It gets the tools that only read.
        // (Only a file with no `tools` line at all takes them all.)
        // (The frontmatter reader can lose a key whose block it cannot make sense of; the line itself is proof it was written.)
        if (tools is null && (requested.Count > 0 || toolsLine || doc.Fields.ContainsKey("tools") || doc.Lists.ContainsKey("tools"))) tools = ReadOnlyTools;
        var disallowed = ResolveTools([.. doc.List("disallowedTools"), .. doc.List("disallowed_tools"), .. doc.List("disallowed-tools")]);
        var readOnly = doc.Bool("readonly") == true || doc.Bool("read-only") == true;
        if (readOnly && tools is null) tools = ReadOnlyTools;
        int? steps = int.TryParse(doc["max_steps"] ?? doc["maxSteps"] ?? doc["max-steps"], out var n) && n > 0 ? Math.Min(n, 200) : null;
        var server = (doc["server"] ?? doc["model"])?.Trim();
        // Claude Code's model shortcuts ("sonnet", "inherit") name nothing a DSH server could match.
        if (server is "inherit" or "sonnet" or "opus" or "haiku" or "fast" or "") server = null;
        return new AgentDefinition
        {
            Name = name,
            Description = description.Length > 0 ? description : TextUtil.Prefix(body, 140),
            Prompt = body,
            Tools = tools,
            DisallowedTools = disallowed,
            Server = server,
            MaxSteps = steps,
            ReadOnly = readOnly,
            Origin = origin,
            Path = path,
        };
    }

    /// <summary>Whether the frontmatter has a top-level line for <paramref name="key"/>, read straight from the text.</summary>
    private static bool HasTopLevelKey(string text, string key)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() is "---" or "...") return false;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#' && line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                var after = line[key.Length..].TrimStart();
                if (after.StartsWith(':')) return true;
            }
        }
        return false;
    }

    /// <summary>Claude-style tool names (Read, Bash, WebFetch) → DSH's; unknown names are dropped. Null when
    /// none of them mean anything here (the caller decides what that means — for an allow list, only the read tools).</summary>
    internal static IReadOnlyList<string>? ResolveTools(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0) return null;
        var resolved = new List<string>();
        foreach (var raw in requested)
        {
            var key = raw.Trim().Trim('"', '\'').ToLowerInvariant().Replace("-", "_");
            var mapped = key switch
            {
                "read" or "readfile" or "read_file" or "view" => "read_file",
                "read_many_files" or "readmanyfiles" => "read_many_files",
                "grep" or "search" => "grep",
                "glob" or "find" => "glob",
                "ls" or "list" or "listdirectory" or "list_directory" => "list_directory",
                "bash" or "shell" or "run_shell_command" or "powershell" => "run_shell_command",
                "edit" or "multiedit" => "edit",
                "write" or "write_file" or "writefile" => "write_file",
                "webfetch" or "web_fetch" or "websearch" => "web_fetch",
                "todowrite" or "todo_write" => "todo_write",
                "memory_search" or "memory_save" or "memory_forget" or "use_skill" or "vault_search"
                    or "process_start" or "process_read" or "process_write" or "process_stop" or "process_list" => key,
                _ => null,
            };
            if (mapped is not null && !resolved.Contains(mapped)) resolved.Add(mapped);
        }
        return resolved.Count == 0 ? null : resolved;
    }

    private static string Slug(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) text.Append(c);
            else if (c is ' ' or '-' or '_' && text.Length > 0 && text[^1] != '-') text.Append('-');
        }
        return text.ToString().Trim('-');
    }
}

/// <summary>The agent types a chat can use: the built-in ones, then <c>.md</c> files from the user's and the
/// project's agent folders, later ones replacing earlier ones of the same name.</summary>
public sealed class AgentCatalog
{
    public static AgentCatalog Default { get; } = new(AgentDefinition.BuiltIn);

    private readonly IReadOnlyList<AgentDefinition> _all;

    public AgentCatalog(IEnumerable<AgentDefinition> all) => _all = all.ToList();

    public IReadOnlyList<AgentDefinition> All => _all;

    /// <summary>The type called <paramref name="name"/> (case-insensitive); the general type for null or empty; null if unknown.</summary>
    public AgentDefinition? Find(string? name)
    {
        var key = string.IsNullOrWhiteSpace(name) ? AgentDefinition.General : name.Trim();
        return _all.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the type called <paramref name="name"/> can only read (an unknown name is a general agent, which can write).</summary>
    public bool ReadOnlyType(string? name) => Find(name)?.IsReadOnly == true;

    /// <summary>"name: description" lines for the agent tool's description, so the model knows its choices.</summary>
    public string Describe()
    {
        var lines = _all.Take(10).Select(a => $"{a.Name}: {TextUtil.Prefix(a.Description, 130)}");
        return string.Join("; ", lines);
    }

    /// <summary>Load built-ins, then every agent file in <paramref name="userDirectories"/>, then in the
    /// project's <c>.dsh/agents</c> and <c>.claude/agents</c> (project files win). Unreadable files are skipped.</summary>
    public static AgentCatalog Load(string? projectRoot, IEnumerable<string>? userDirectories = null)
    {
        var byName = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var built in AgentDefinition.BuiltIn) byName[built.Name] = built;
        foreach (var directory in userDirectories ?? DefaultUserDirectories())
            Merge(byName, directory, "user");
        if (!string.IsNullOrEmpty(projectRoot))
        {
            Merge(byName, Path.Combine(projectRoot, ".claude", "agents"), "project");
            Merge(byName, Path.Combine(projectRoot, ".dsh", "agents"), "project");
        }
        var builtInOrder = AgentDefinition.BuiltIn.Select(a => a.Name).ToList();
        var ordered = byName.Values
            .OrderBy(a => builtInOrder.IndexOf(a.Name) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        return new AgentCatalog(ordered);
    }

    /// <summary>Changes when an agent file is added, removed or edited in the folders <see cref="Load"/> reads — cheap
    /// enough to ask before every turn, so a file written while the app is running is picked up.</summary>
    public static string Stamp(string? projectRoot, IEnumerable<string>? userDirectories = null)
    {
        var folders = (userDirectories ?? DefaultUserDirectories()).ToList();
        if (!string.IsNullOrEmpty(projectRoot))
        {
            folders.Add(Path.Combine(projectRoot, ".claude", "agents"));
            folders.Add(Path.Combine(projectRoot, ".dsh", "agents"));
        }
        long stamp = 17;
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    // Summed, not chained, so the order the disk lists files in doesn't matter.
                    stamp += StringComparer.OrdinalIgnoreCase.GetHashCode(file) ^ info.LastWriteTimeUtc.Ticks ^ info.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable folder: as good as absent.
            }
        }
        return stamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static IEnumerable<string> DefaultUserDirectories()
    {
        yield return Path.Combine(AppPaths.Root, "agents");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) yield return Path.Combine(home, ".claude", "agents");
    }

    private static void Merge(Dictionary<string, AgentDefinition> byName, string directory, string origin)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory)) return;
            files = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                if (new FileInfo(file).Length > 200_000) continue;
                var parsed = AgentDefinition.Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), origin, file);
                if (parsed is not null) byName[parsed.Name] = parsed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unreadable file must not hide the rest.
            }
        }
    }
}
