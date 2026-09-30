using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsh.Core;

/// <summary>Finds what Claude Code and Cursor have installed on this PC and works out, for each item,
/// what DSH would do with it. Read-only: nothing is copied, changed or run. Slow-ish on a big profile
/// (it lists folders), so the app calls it off the UI thread.</summary>
public static partial class ExternalScanner
{
    /// <summary>The name of the instruction file imported from Claude Code's CLAUDE.md.</summary>
    public const string ClaudeInstructionsTarget = "claude-code";

    public static ExternalInventory Scan(ExternalLocations external, SkillLocations dsh, CancellationToken cancellationToken = default) =>
        new Run(external, dsh, cancellationToken).Scan();

    /// <summary>Where an item stands against the skills DSH has: already there under its own name or a
    /// numbered copy's (-2, -3 … made when an import kept both), waiting in the approval queue, a
    /// different skill of that name, or new. <paramref name="render"/> gives the SKILL.md the item would
    /// write under a folder name — a numbered copy carries its number as its name, so it has to be asked
    /// per name.</summary>
    internal static ExternalStatus SkillStatus(SkillLocations dsh, string target, Func<string, string?> render, DraftIndex? drafts = null)
    {
        var any = false;
        for (var i = 1; i <= 9; i++)
        {
            var name = i == 1 ? target : $"{target}-{i}";
            var folder = Path.Combine(dsh.UserSkills, name);
            if (!Directory.Exists(folder)) continue;
            any = true;
            if (ExternalConverters.SameText(SkillFiles.ReadText(Path.Combine(folder, "SKILL.md")), render(name))) return ExternalStatus.Imported;
        }
        if ((drafts ?? DraftIndex.Read(dsh)).Holds(target, render(target))) return ExternalStatus.Waiting;
        return any ? ExternalStatus.Different : ExternalStatus.New;
    }

    /// <summary>The skills waiting in the approval queue, read once so a scan (or an import of many) doesn't
    /// reread them for every item.</summary>
    internal sealed class DraftIndex
    {
        private readonly List<(string Name, string Text)> _drafts = [];

        public static DraftIndex Read(SkillLocations dsh)
        {
            var index = new DraftIndex();
            foreach (var draft in SkillDrafts.List(dsh))
            {
                if (SkillFiles.ReadText(draft.SkillFile) is { } text) index._drafts.Add((draft.Name, text));
            }
            return index;
        }

        public void Add(string name, string text) => _drafts.Add((name, text));

        public bool Holds(string name, string? text) =>
            text is not null && _drafts.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && ExternalConverters.SameText(d.Text, text));
    }

    internal static ExternalStatus InstructionsStatus(SkillLocations dsh, string target, string? text)
    {
        var file = Path.Combine(dsh.UserInstructions, target + ".md");
        if (!File.Exists(file)) return ExternalStatus.New;
        return ExternalConverters.SameText(SkillFiles.ReadText(file), text) ? ExternalStatus.Imported : ExternalStatus.Different;
    }

    internal static ExternalStatus NotesStatus(SkillLocations dsh, string target, IReadOnlyList<ExternalConverters.Note> notes)
    {
        var folder = Path.Combine(dsh.ProjectNotes, target);
        var same = 0;
        var any = false;
        foreach (var note in notes)
        {
            var copy = Path.Combine([folder, .. note.Relative.Split('/')]);
            if (!File.Exists(copy)) continue;
            any = true;
            if (ExternalConverters.SameText(SkillFiles.ReadText(copy), SkillFiles.ReadText(note.FullPath))) same++;
        }
        if (!any) return ExternalStatus.New;
        return same == notes.Count ? ExternalStatus.Imported : ExternalStatus.Different;
    }

    // MARK: - The scan

    private sealed class Run(ExternalLocations external, SkillLocations dsh, CancellationToken cancellationToken)
    {
        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static readonly JsonDocumentOptions JsonOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private const string SecretWarning = "Looks like it contains a key or token. It would go to your model provider with every message.";
        private const long MaxJsonBytes = 4_000_000;

        private readonly List<ExternalItem> _items = [];
        private readonly List<ExternalNote> _left = [];
        private readonly List<ExternalToolInfo> _tools = [];
        private readonly HashSet<string> _seen = new(PathComparer);
        /// <summary>What each skill-like item would write (its SKILL.md), by item id.</summary>
        private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
        /// <summary>Plugins that also ship hooks or MCP servers, and plugin files that are links — per tool.</summary>
        private readonly Dictionary<ExternalTool, int> _pluginsWithExtras = new();
        private readonly Dictionary<ExternalTool, int> _linksLeftOut = new();
        private readonly DraftIndex _drafts = DraftIndex.Read(dsh);
        private void Count(Dictionary<ExternalTool, int> counter, ExternalTool tool) => counter[tool] = counter.GetValueOrDefault(tool) + 1;

        public ExternalInventory Scan()
        {
            Guard(ExternalTool.ClaudeCode, "its folder", Claude);
            Guard(ExternalTool.Cursor, "its folder", Cursor);
            MarkDuplicates();
            foreach (var (tool, count) in _pluginsWithExtras)
                _left.Add(new ExternalNote(tool, $"{count} plugin{(count == 1 ? " also ships" : "s also ship")} hooks or MCP servers — DSH can't use those."));
            foreach (var (tool, count) in _linksLeftOut)
                _left.Add(new ExternalNote(tool, $"{count} item{(count == 1 ? " in a plugin links" : "s in plugins link")} to somewhere else on this PC, so {(count == 1 ? "it was" : "they were")} left out."));
            return new ExternalInventory(_tools, _items, _left);
        }

        /// <summary>Run one part of the scan. If something in it can't be read — a folder that vanished, a
        /// path a manifest made up — the rest still shows, with a line saying what was missed.</summary>
        private void Guard(ExternalTool tool, string what, Action scan)
        {
            try
            {
                scan();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                           or System.Security.SecurityException)
            {
                _left.Add(new ExternalNote(tool, $"Couldn't read everything in {what}: {ex.Message}"));
            }
        }

        // MARK: Claude Code

        private void Claude()
        {
            var home = external.ClaudeHome;
            var found = Directory.Exists(home) || File.Exists(external.ClaudeGlobalConfig);
            _tools.Add(new ExternalToolInfo(ExternalTool.ClaudeCode, home, found));
            if (!found) return;
            cancellationToken.ThrowIfCancellationRequested();

            AddInstructions(ExternalTool.ClaudeCode, Path.Combine(home, "CLAUDE.md"), ClaudeInstructionsTarget);
            AddFolder(ExternalTool.ClaudeCode, home, "Your", reasonOff: null, pluginRoot: null);
            AddClaudePlugins(home);
            AddClaudeNotes(home);
            cancellationToken.ThrowIfCancellationRequested();

            var servers = McpServerNames(external.ClaudeGlobalConfig);
            if (servers.Count > 0) _left.Add(new ExternalNote(ExternalTool.ClaudeCode, McpNote(servers)));
            if (ReadJson(Path.Combine(home, "settings.json")) is { } settings)
            {
                using (settings)
                {
                    if (settings.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (settings.RootElement.TryGetProperty("hooks", out var hooks) && hooks.ValueKind == JsonValueKind.Object
                            && hooks.EnumerateObject().Any())
                            _left.Add(new ExternalNote(ExternalTool.ClaudeCode, "Claude Code hooks — DSH has no hooks."));
                        if (settings.RootElement.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
                            && permissions.EnumerateObject().Any())
                            _left.Add(new ExternalNote(ExternalTool.ClaudeCode,
                                "Claude Code's allow/deny rules for tools — DSH asks with its own permission presets instead."));
                    }
                }
            }
            var conversations = CountConversations(Path.Combine(home, "projects"));
            if (conversations > 0)
                _left.Add(new ExternalNote(ExternalTool.ClaudeCode,
                    $"{conversations:N0} Claude Code conversation{(conversations == 1 ? "" : "s")} — chat history stays where it is."));
        }

        private void AddClaudeNotes(string home)
        {
            var projects = Path.Combine(home, "projects");
            foreach (var directory in Directories(projects))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var memory = Path.Combine(directory, "memory");
                if (!Directory.Exists(memory)) continue;
                var notes = ExternalConverters.NoteFiles(memory);
                if (notes.Count == 0) continue;

                var key = Path.GetFileName(directory);
                var project = ClaudeProjectNames.FindFolder(key);
                var warnings = new List<string>();
                var secret = notes.Any(n => SkillFiles.ReadText(n.FullPath) is { } text && SkillLint.ContainsSecret(text));
                if (secret) warnings.Add(SecretWarning);
                var name = project is null ? ClaudeProjectNames.Readable(key) : Path.GetFileName(project);
                var place = project is null ? "its folder isn't on this PC" : project;
                var status = NotesStatus(dsh, key, notes);
                var reason = project is null ? "The project's folder isn't on this PC, so nothing would use these notes here."
                    : secret ? "Looks like it contains a key or token."
                    : status == ExternalStatus.Different
                        ? "Claude Code has changed these notes since you brought them in. Importing updates DSH's copy and keeps the old files beside it as .bak."
                        : null;
                _items.Add(new ExternalItem
                {
                    Id = ItemId(ExternalTool.ClaudeCode, ExternalKind.ProjectNotes, directory),
                    Tool = ExternalTool.ClaudeCode,
                    Kind = ExternalKind.ProjectNotes,
                    Name = name,
                    Description = $"{notes.Count} note{(notes.Count == 1 ? "" : "s")} · {place}",
                    SourcePath = memory,
                    Group = "Notes Claude Code saved per project",
                    FileCount = notes.Count,
                    Bytes = notes.Sum(n => n.Bytes),
                    Warnings = warnings,
                    ContainsSecret = secret,
                    Status = status,
                    // Only new notes are ticked: an update could replace a note edited in DSH, so it is the user's call.
                    SelectedByDefault = reason is null && status == ExternalStatus.New,
                    Reason = reason,
                    ProjectPath = project,
                    Target = key,
                });
            }
        }

        private void AddClaudePlugins(string home)
        {
            var enabled = EnabledPlugins(Path.Combine(home, "settings.json"));
            var installed = InstalledClaudePlugins(home);
            foreach (var (key, root, onlyFor) in installed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var off = enabled.TryGetValue(key, out var on) && !on ? "This plugin is turned off in Claude Code."
                    : onlyFor is not null ? $"Installed in Claude Code for one project only ({onlyFor})." : null;
                Guard(ExternalTool.ClaudeCode, $"the plugin {key.Split('@')[0]}", () => AddPlugin(ExternalTool.ClaudeCode, root, key.Split('@')[0], off));
            }
        }

        /// <summary>The installed plugins: what installed_plugins.json lists (both file versions) — and only
        /// that, even when it lists none, since the cache keeps uninstalled versions around. Without a
        /// readable list, the newest folder of each plugin in the cache. <c>OnlyFor</c> is set when Claude
        /// Code installed it for one project rather than for the user.</summary>
        private static List<(string Key, string Root, string? OnlyFor)> InstalledClaudePlugins(string home)
        {
            var output = new List<(string, string, string?)>();
            var listed = false;
            var seen = new HashSet<string>(PathComparer);
            if (ReadJson(Path.Combine(home, "plugins", "installed_plugins.json")) is { } doc)
            {
                using (doc)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
                    {
                        listed = true;
                        foreach (var plugin in plugins.EnumerateObject())
                        {
                            // Version 2 lists each install; version 1 has just the one.
                            var entries = plugin.Value.ValueKind == JsonValueKind.Array ? plugin.Value.EnumerateArray().ToList() : [plugin.Value];
                            foreach (var entry in entries)
                            {
                                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("installPath", out var path)
                                    || path.ValueKind != JsonValueKind.String || path.GetString() is not { Length: > 0 } root
                                    || !Directory.Exists(root)) continue;
                                string? onlyFor = null;
                                if (entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String
                                    && scope.GetString() is "project" or "local")
                                    onlyFor = entry.TryGetProperty("projectPath", out var where) && where.ValueKind == JsonValueKind.String
                                        ? where.GetString() : "a project";
                                if (seen.Add(Path.GetFullPath(root))) output.Add((plugin.Name, root, onlyFor));
                            }
                        }
                    }
                }
            }
            if (listed || output.Count > 0) return output;

            // No list to go by: whatever the cache holds, newest version of each plugin.
            foreach (var market in Directories(Path.Combine(home, "plugins", "cache")))
            {
                foreach (var plugin in Directories(market))
                {
                    if (NewestVersion(plugin) is { } root && seen.Add(Path.GetFullPath(root)))
                        output.Add(($"{Path.GetFileName(plugin)}@{Path.GetFileName(market)}", root, null));
                }
            }
            return output;
        }

        private static Dictionary<string, bool> EnabledPlugins(string settingsFile)
        {
            var output = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (ReadJson(settingsFile) is not { } doc) return output;
            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("enabledPlugins", out var enabled) && enabled.ValueKind == JsonValueKind.Object)
                {
                    foreach (var plugin in enabled.EnumerateObject())
                    {
                        if (plugin.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            output[plugin.Name] = plugin.Value.GetBoolean();
                    }
                }
            }
            return output;
        }

        private static int CountConversations(string projects)
        {
            var count = 0;
            foreach (var directory in Directories(projects))
            {
                try
                {
                    count += Directory.EnumerateFiles(directory, "*.jsonl").Count();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable folder: not counted.
                }
            }
            return count;
        }

        // MARK: Cursor

        private void Cursor()
        {
            var home = external.CursorHome;
            var found = Directory.Exists(home);
            _tools.Add(new ExternalToolInfo(ExternalTool.Cursor, home, found));
            if (!found) return;
            cancellationToken.ThrowIfCancellationRequested();

            AddFolder(ExternalTool.Cursor, home, "Your", reasonOff: null, pluginRoot: null);

            // Skills that ship inside the Cursor app itself: they're about Cursor, not about your work.
            var builtIn = new SkillRoot(Path.Combine(home, "skills-cursor"), 0, SkillOrigin.Cursor, SkillScope.User, SkillRootLayout.SkillDirs);
            foreach (var skill in SkillCatalog.ScanSkillDirs(builtIn))
                AddSkillLike(ExternalTool.Cursor, ExternalKind.Skill, skill, "Cursor's built-in skills", "Built into the Cursor app; mostly about Cursor itself.", null);

            var plugins = Path.Combine(home, "plugins");
            foreach (var market in Directories(Path.Combine(plugins, "cache")))
            {
                foreach (var plugin in Directories(market))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (NewestVersion(plugin) is { } root)
                        Guard(ExternalTool.Cursor, $"the plugin {Path.GetFileName(plugin)}", () => AddPlugin(ExternalTool.Cursor, root, Path.GetFileName(plugin), null));
                }
            }
            foreach (var local in Directories(Path.Combine(plugins, "local")))
                Guard(ExternalTool.Cursor, $"the plugin {Path.GetFileName(local)}", () => AddPlugin(ExternalTool.Cursor, local, Path.GetFileName(local), null));

            var servers = McpServerNames(Path.Combine(home, "mcp.json"));
            if (servers.Count > 0) _left.Add(new ExternalNote(ExternalTool.Cursor, McpNote(servers)));
            _left.Add(new ExternalNote(ExternalTool.Cursor,
                "Cursor's User Rules and saved Memories live inside the app, not in a folder DSH can read. Copy the text from Cursor's settings if you want it here."));
        }

        // MARK: Shared shapes

        /// <summary>The skills, commands, rules and subagents in a tool's folder (or a plugin's).</summary>
        private void AddFolder(ExternalTool tool, string root, string groupPrefix, string? reasonOff, string? pluginRoot,
                               IEnumerable<string>? extraSkills = null, IEnumerable<string>? extraCommands = null,
                               IEnumerable<string>? extraAgents = null)
        {
            var origin = tool == ExternalTool.ClaudeCode ? SkillOrigin.Claude : SkillOrigin.Cursor;
            SkillRoot RootAt(string path, SkillRootLayout layout, bool claudeRules = false) =>
                new(path, 0, origin, SkillScope.User, layout, claudeRules);

            // A plugin is someone else's content: if one of its content folders is a link (or sits behind one)
            // everything under the target would be listed, so it is left out — and said so.
            bool Usable(string directory)
            {
                if (pluginRoot is null || !ThroughLink(pluginRoot, directory)) return true;
                Count(_linksLeftOut, tool);
                return false;
            }

            foreach (var directory in Merge(Path.Combine(root, "skills"), extraSkills).Where(Usable))
                foreach (var skill in SkillCatalog.ScanSkillDirs(RootAt(directory, SkillRootLayout.SkillDirs)))
                    AddSkillLike(tool, ExternalKind.Skill, skill, $"{groupPrefix} skills", reasonOff, pluginRoot);

            foreach (var directory in Merge(Path.Combine(root, "commands"), extraCommands).Where(Usable))
                foreach (var command in SkillCatalog.ScanCommands(RootAt(directory, SkillRootLayout.CommandFiles)))
                    AddSkillLike(tool, ExternalKind.Command, command, $"{groupPrefix} commands", reasonOff, pluginRoot);

            var rules = Path.Combine(root, "rules");
            if (Usable(rules))
            {
                foreach (var rule in SkillCatalog.ScanRules(RootAt(rules, SkillRootLayout.RuleFiles, claudeRules: tool == ExternalTool.ClaudeCode),
                                                            claudeStyle: tool == ExternalTool.ClaudeCode))
                    AddSkillLike(tool, ExternalKind.Rule, rule, $"{groupPrefix} rules", reasonOff, pluginRoot);
            }

            foreach (var directory in Merge(Path.Combine(root, "agents"), extraAgents).Where(Usable))
                AddSubagents(tool, directory, $"{groupPrefix} subagents", reasonOff, pluginRoot);
        }

        /// <summary>A plugin folder: what its manifest names, plus the standard folders.</summary>
        private void AddPlugin(ExternalTool tool, string root, string fallbackName, string? reasonOff)
        {
            if (!_seen.Add("plugin|" + Path.GetFullPath(root))) return;
            JsonDocument? manifest = null;
            foreach (var candidate in new[] { ".claude-plugin", ".cursor-plugin", ".codex-plugin", "" })
            {
                manifest = ReadJson(Path.Combine(root, candidate, "plugin.json"));
                if (manifest is not null) break;
            }
            using (manifest)
            {
                var name = fallbackName;
                if (manifest?.RootElement is { ValueKind: JsonValueKind.Object } top
                    && top.TryGetProperty("name", out var declared) && declared.ValueKind == JsonValueKind.String
                    && declared.GetString() is { Length: > 0 } text)
                    name = text;

                // Folders (or files) the manifest names for a kind of content, kept inside the plugin.
                List<string> Declared(string property)
                {
                    var found = new List<string>();
                    if (manifest?.RootElement is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(property, out var value))
                        return found;
                    var paths = new List<string?>();
                    if (value.ValueKind == JsonValueKind.String) paths.Add(value.GetString());
                    else if (value.ValueKind == JsonValueKind.Array)
                        paths.AddRange(value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()));
                    foreach (var path in paths)
                    {
                        if (InsideFolder(root, path) is { } inside) found.Add(inside);
                    }
                    return found;
                }

                var before = _items.Count;
                AddFolder(tool, root, $"Plugin {name}:", reasonOff, root, Declared("skills"), Declared("commands"), Declared("agents"));
                // The groups above read "Plugin x: skills"; tidy them to one group per plugin.
                for (var i = before; i < _items.Count; i++)
                    _items[i] = _items[i] with { Group = $"Plugin: {name}" };

                if (File.Exists(Path.Combine(root, ".mcp.json")) || File.Exists(Path.Combine(root, "mcp.json"))
                    || File.Exists(Path.Combine(root, "hooks", "hooks.json"))
                    || (manifest?.RootElement is { ValueKind: JsonValueKind.Object } o && (o.TryGetProperty("hooks", out _) || o.TryGetProperty("mcpServers", out _))))
                    Count(_pluginsWithExtras, tool);
            }
        }

        private void AddSubagents(ExternalTool tool, string directory, string group, string? reasonOff, string? pluginRoot)
        {
            foreach (var entry in FileWalk.Entries(directory).Where(e => !e.IsDirectory && e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (pluginRoot is not null && entry.IsLink)
                {
                    Count(_linksLeftOut, tool);
                    continue;
                }
                if (SkillFiles.ReadText(entry.FullPath) is not { } source) continue;
                var converted = ExternalConverters.SubagentSkill(tool, entry.Name, source);
                if (converted is null) continue;
                var (slug, text) = converted.Value;
                var doc = SkillDocument.Parse(text);
                var id = ItemId(tool, ExternalKind.Subagent, entry.FullPath);
                if (!_seen.Add(id)) continue;
                _texts[id] = text;
                var (warnings, secret) = Vet(source, pluginRoot);
                var fileName = entry.Name;
                Add(new ExternalItem
                {
                    Id = id,
                    Tool = tool,
                    Kind = ExternalKind.Subagent,
                    Name = SkillDocument.Parse(source)["name"]?.Trim() is { Length: > 0 } named ? named : slug,
                    Description = doc["description"] ?? "",
                    SourcePath = entry.FullPath,
                    Group = group,
                    Bytes = source.Length,
                    Warnings = warnings,
                    ContainsSecret = secret,
                    Target = slug,
                }, reasonOff, name => ExternalConverters.SubagentSkill(tool, fileName, source, name)?.Text);
            }
        }

        private void AddSkillLike(ExternalTool tool, ExternalKind kind, Skill skill, string group, string? reasonOff, string? pluginRoot)
        {
            var location = kind == ExternalKind.Skill ? skill.Directory : skill.Path;
            // A plugin is someone else's content: a link in it could point at any file on this PC, and
            // whatever it points at would become the skill's text. Your own folders are yours to link.
            if (pluginRoot is not null && (SkillFiles.IsLink(skill.Path) || (kind == ExternalKind.Skill && SkillFiles.IsLink(skill.Directory))))
            {
                Count(_linksLeftOut, tool);
                return;
            }
            var id = ItemId(tool, kind, location);
            if (!_seen.Add(kind + "|" + RealPath(location))) return; // the same folder reached twice (a link)
            var slug = SkillFiles.NonEmpty(SkillNaming.Slug(skill.Name), "imported");
            var text = SkillConverter.SkillFileText(skill, slug);
            var source = SkillFiles.ReadText(skill.Path);
            if (text is null || source is null) return; // unreadable
            var candidate = SkillImporter.Candidate(skill);
            _texts[id] = text;
            // A skill folder can carry files of its own (a .env someone left in it): they are copied too.
            var (warnings, secret) = Vet(source, pluginRoot, kind == ExternalKind.Skill ? skill.Directory : null);
            Add(new ExternalItem
            {
                Id = id,
                Tool = tool,
                Kind = kind,
                Name = skill.Name,
                Description = skill.Description,
                SourcePath = skill.Path,
                Group = group,
                FileCount = candidate.FileCount,
                Bytes = candidate.TotalBytes,
                HasScripts = candidate.HasScripts,
                Warnings = warnings,
                ContainsSecret = secret,
                Target = slug,
                Skill = skill,
            }, reasonOff, name => SkillConverter.SkillFileText(skill, name));
        }

        private void AddInstructions(ExternalTool tool, string file, string target)
        {
            if (!File.Exists(file) || SkillFiles.ReadText(file) is not { } text || text.Trim().Length == 0) return;
            var id = ItemId(tool, ExternalKind.Instructions, file);
            if (!_seen.Add(id)) return;
            var warnings = new List<string>();
            var secret = SkillLint.ContainsSecret(text);
            if (secret) warnings.Add(SecretWarning);
            if (IncludesFiles().IsMatch(text))
                warnings.Add("Pulls in other files with @path lines. DSH doesn't follow those, so the copy has the @path lines but not the files.");
            _texts[id] = text;
            var lines = text.Replace("\r\n", "\n").TrimEnd().Split('\n').Length;
            Add(new ExternalItem
            {
                Id = id,
                Tool = tool,
                Kind = ExternalKind.Instructions,
                Name = "Your global instructions (CLAUDE.md)",
                Description = $"{lines:N0} line{(lines == 1 ? "" : "s")} · read on every request, in every project",
                SourcePath = file,
                Group = "Your instructions",
                Bytes = text.Length,
                Warnings = warnings,
                ContainsSecret = secret,
                Target = target,
            }, null, null);
        }

        /// <summary>Work out status and the default tick, then keep the item.</summary>
        private void Add(ExternalItem item, string? optionalReason, Func<string, string?>? render)
        {
            var status = item.Kind == ExternalKind.Instructions
                ? InstructionsStatus(dsh, item.Target, _texts.GetValueOrDefault(item.Id))
                : SkillStatus(dsh, item.Target, render ?? (_ => null), _drafts);
            var reason = optionalReason ?? (item.ContainsSecret ? "Looks like it contains a key or token." : null);
            if (reason is null && status == ExternalStatus.Different)
                reason = "DSH already has a different one with this name. Importing keeps both.";
            _items.Add(item with { Status = status, SelectedByDefault = reason is null && status == ExternalStatus.New, Reason = reason });
        }

        /// <summary>The same content reached by more than one route (a skill copied into both tools):
        /// the first stays ticked, the others say so.</summary>
        private void MarkDuplicates()
        {
            var first = new Dictionary<string, ExternalItem>(StringComparer.Ordinal);
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!_texts.TryGetValue(item.Id, out var text)) continue;
                var key = $"{item.Kind}|{item.Target}|{text.Replace("\r\n", "\n").TrimEnd()}";
                if (!first.TryAdd(key, item))
                {
                    _items[i] = item with
                    {
                        SelectedByDefault = false,
                        Reason = item.Status == ExternalStatus.Imported ? item.Reason : $"Same as the copy from {first[key].Tool.Label()}.",
                    };
                }
            }
        }

        // MARK: Little helpers

        private static string ItemId(ExternalTool tool, ExternalKind kind, string path) => $"{tool}|{kind}|{SkillFiles.Normalize(path)}";

        /// <summary>What to tell the user about a skill's text (and, for a skill folder, the files bundled
        /// with it), and whether anything in it looks like a secret.</summary>
        private static (List<string> Warnings, bool Secret) Vet(string source, string? pluginRoot, string? bundledFolder = null)
        {
            var warnings = new List<string>();
            var secret = SkillLint.ContainsSecret(source);
            if (secret) warnings.Add(SecretWarning);
            if (bundledFolder is not null && BundledSecret(bundledFolder) is { } file)
            {
                secret = true;
                warnings.Add($"“{file}” inside it looks like a credentials file or holds a key or token. It would be copied with the skill.");
            }
            if (pluginRoot is not null && PluginRootReference().IsMatch(source))
                warnings.Add("Refers to files in its plugin's folder, which aren't copied with it.");
            return (warnings, secret);
        }

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".pdf", ".zip", ".gz", ".tar", ".7z", ".exe", ".dll", ".so",
            ".dylib", ".woff", ".woff2", ".ttf", ".otf", ".mp3", ".mp4", ".mov", ".wasm", ".bin", ".onnx", ".pyc",
        };

        /// <summary>A file named like credentials (.env, id_rsa, *.pem …), or holding something that looks
        /// like a key, among the files bundled in a skill folder — as a path relative to the folder; null
        /// when there is none. Bounded, since a skill is small; links are never read.</summary>
        private static string? BundledSecret(string directory)
        {
            var checkedFiles = 0;

            static bool NamedLikeCredentials(string name)
            {
                var lower = name.ToLowerInvariant();
                if (lower.StartsWith(".env", StringComparison.Ordinal))
                    return !(lower.EndsWith(".example", StringComparison.Ordinal) || lower.EndsWith(".sample", StringComparison.Ordinal)
                             || lower.EndsWith(".template", StringComparison.Ordinal));
                return lower is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" or ".npmrc" or ".netrc" or "credentials" or "credentials.json"
                           or "secrets.json" or "secrets.yml" or "secrets.yaml"
                       || lower.EndsWith(".pem", StringComparison.Ordinal) || lower.EndsWith(".key", StringComparison.Ordinal)
                       || lower.EndsWith(".pfx", StringComparison.Ordinal) || lower.EndsWith(".p12", StringComparison.Ordinal);
            }

            string? Visit(DirectoryInfo folder, string prefix, int depth)
            {
                if (depth > 4) return null;
                FileSystemInfo[] entries;
                try
                {
                    entries = folder.GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return null;
                }
                foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    if (checkedFiles >= 80) return null;
                    if (SkillFiles.IsLink(entry) || SkillFiles.SkipNames.Contains(entry.Name)) continue;
                    var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                    if (entry is DirectoryInfo sub)
                    {
                        if (Visit(sub, relative, depth + 1) is { } inner) return inner;
                        continue;
                    }
                    if (entry is not FileInfo file || relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                    checkedFiles++;
                    if (NamedLikeCredentials(file.Name)) return relative;
                    if (BinaryExtensions.Contains(file.Extension)) continue;
                    try
                    {
                        if (file.Length > 200_000) continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    if (SkillFiles.ReadText(file.FullName) is { } text && SkillLint.ContainsSecret(text)) return relative;
                }
                return null;
            }

            return Visit(new DirectoryInfo(directory), "", 0);
        }

        /// <summary>Whether <paramref name="directory"/>, or any folder between it and <paramref name="root"/>,
        /// is a link — something that points somewhere else and so isn't part of the plugin.</summary>
        private static bool ThroughLink(string root, string directory)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            while (current.Length > top.Length && current.StartsWith(top, comparison))
            {
                if (SkillFiles.IsLink(current)) return true;
                current = Path.GetDirectoryName(current) ?? "";
            }
            return false;
        }

        private static string RealPath(string path)
        {
            try
            {
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                return (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Path.GetFullPath(path);
            }
        }

        /// <summary>The standard folder plus any the manifest names, existing ones only.</summary>
        private static IEnumerable<string> Merge(string standard, IEnumerable<string>? extra)
        {
            var seen = new HashSet<string>(PathComparer);
            foreach (var path in new[] { standard }.Concat(extra ?? []))
            {
                if (Directory.Exists(path) && seen.Add(Path.GetFullPath(path))) yield return path;
            }
        }

        /// <summary>A path from a plugin manifest, resolved under the plugin's folder — never outside it.</summary>
        private static string? InsideFolder(string root, string? relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
        }

        private static IEnumerable<string> Directories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path).OrderBy(p => p, StringComparer.Ordinal).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        /// <summary>The most recently written version folder of a plugin that holds anything of use.</summary>
        private static string? NewestVersion(string pluginFolder) =>
            Directories(pluginFolder)
                .Where(v => new[] { "skills", "commands", "agents", "rules" }.Any(d => Directory.Exists(Path.Combine(v, d))))
                .OrderByDescending(v => Directory.GetLastWriteTimeUtc(v))
                .FirstOrDefault();

        private static JsonDocument? ReadJson(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaxJsonBytes) return null;
                return JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>The names of the MCP servers a config file lists — names only, never their settings
        /// (those hold tokens).</summary>
        private static List<string> McpServerNames(string file)
        {
            var names = new List<string>();
            if (ReadJson(file) is not { } doc) return names;
            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
                    names.AddRange(servers.EnumerateObject().Select(s => s.Name));
            }
            return names;
        }

        private static string McpNote(List<string> names)
        {
            var shown = string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {names.Count - 5} more" : "");
            return $"{names.Count} MCP server{(names.Count == 1 ? "" : "s")} ({shown}) — DSH can't run MCP servers yet.";
        }
    }

    // MARK: - Patterns

    /// <summary>"@path/to/file.md" — Claude Code's include syntax. Needs a slash or a file extension, so
    /// @mentions and email addresses don't count.</summary>
    [GeneratedRegex(@"(?<![\w@`])@(?:~|\.{1,2})?[\w./\\-]*(?:[/\\][\w.-]+|\.(?:md|txt|json|ya?ml))", RegexOptions.Multiline)]
    private static partial Regex IncludesFiles();

    [GeneratedRegex(@"CLAUDE_PLUGIN_ROOT|CURSOR_PLUGIN_ROOT|PLUGIN_ROOT", RegexOptions.None)]
    private static partial Regex PluginRootReference();
}
