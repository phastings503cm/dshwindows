using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Reading an OpenClaw install
//
// Everything is read, nothing is changed, and nothing is run. What comes back is a bundle to show the
// user: skills, memory notes, keys and passwords, model servers — and, said plainly, what was found but
// isn't imported (chat channels, cron jobs, OAuth sign-ins that belong to OpenClaw's own session).

public static partial class OpenClawReader
{
    private const long MaxConfigBytes = 2_000_000;
    private const long MaxNoteBytes = 512_000;
    private const long MaxSmallFileBytes = 1_000_000;
    private const int MaxDailyFiles = 200;
    private const int DefaultDailyFiles = 45;

    public static async Task<OpenClawBundle> ReadAsync(IFileSource source, OpenClawInstall install,
                                                       IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var left = new List<string>();
        var home = await source.HomeAsync(cancellationToken).ConfigureAwait(false);
        var state = install.StateDir;

        progress?.Report("Reading the configuration…");
        JsonObject? config = null;
        if (install.ConfigPath is not null)
        {
            var text = await source.ReadTextAsync(install.ConfigPath, MaxConfigBytes, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                problems.Add($"Couldn't read {install.ConfigPath}.");
            }
            else if (Json5.TryParse(text, out var error) is JsonObject parsed)
            {
                config = parsed;
            }
            else
            {
                problems.Add($"Couldn't make sense of {System.IO.Path.GetFileName(install.ConfigPath)} ({error}). Skills and memory can still be brought in; the keys inside it can't.");
            }
        }

        var workspaces = await ResolveWorkspacesAsync(source, install, config, home, cancellationToken).ConfigureAwait(false);

        progress?.Report("Looking for skills…");
        var skillRoots = new List<(string Dir, string Location)>();
        foreach (var workspace in workspaces) skillRoots.Add((source.Combine(workspace, "skills"), "workspace"));
        skillRoots.Add((source.Combine(state, "skills"), "managed"));
        if (At(config, "skills", "load", "extraDirs") is JsonArray extra)
        {
            foreach (var node in extra)
            {
                if (Text(node) is { Length: > 0 } dir) skillRoots.Add((ExpandPath(source, dir, home, state), "extra"));
            }
        }
        var skills = await ScanSkillsAsync(source, skillRoots, cancellationToken).ConfigureAwait(false);

        progress?.Report("Reading memory…");
        var noteFiles = await ScanNotesAsync(source, workspaces, cancellationToken).ConfigureAwait(false);

        progress?.Report("Looking for keys and passwords…");
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var collector = new CredentialCollector();
        await ReadEnvFilesAsync(source, state, workspaces, env, cancellationToken).ConfigureAwait(false);
        var servers = ReadModelServers(config, source, collector, env);
        var oauth = await ReadAuthProfilesAsync(source, state, collector, cancellationToken).ConfigureAwait(false);
        SweepConfig(config, env, collector, servers);
        foreach (var (name, value) in env)
        {
            if (IsSecretEnvName(name)) collector.Add(name, value, KindOf(name), ".env", $"From OpenClaw's environment file ({name}).", null, null);
        }

        // What isn't imported.
        if (oauth.Count > 0)
            left.Add($"{oauth.Count} OAuth sign-in{(oauth.Count == 1 ? "" : "s")} ({string.Join(", ", oauth.Distinct().Take(5))}) — they belong to OpenClaw's own session, so DSH can't reuse them.");
        if (At(config, "channels") is JsonObject channels && channels.Count > 0)
            left.Add($"Chat channels ({string.Join(", ", channels.Select(c => c.Key).Take(8))}) — they belong to OpenClaw's gateway.");
        var cron = await CountCronJobsAsync(source, state, cancellationToken).ConfigureAwait(false);
        if (cron > 0) left.Add($"{cron} scheduled job{(cron == 1 ? "" : "s")} (cron) — DSH has the task queue instead; recreate any you still want.");
        var sessions = await CountSessionsAsync(source, state, cancellationToken).ConfigureAwait(false);
        if (sessions > 0) left.Add($"{sessions} saved conversation{(sessions == 1 ? "" : "s")} — chat history isn't brought over.");
        foreach (var unresolved in collector.Unresolved.Distinct().Take(6))
            left.Add($"A setting refers to ${{{unresolved}}} but no value for it was found.");

        return new OpenClawBundle
        {
            Install = install,
            SourceLabel = source.Label,
            Workspaces = workspaces,
            Skills = skills,
            NoteFiles = noteFiles,
            Credentials = collector.Result,
            ModelServers = servers,
            Left = left,
            Problems = problems,
        };
    }

    // MARK: - JSON helpers

    private static JsonNode? At(JsonNode? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(key, out var next)) node = next;
            else return null;
        }
        return node;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A positive whole number (a context window), else null — zero or a negative one is a setting that
    /// says nothing, and Infinity/NaN read as 0.</summary>
    private static int? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var n)) return n > 0 ? n : null;
        return value.TryGetValue<long>(out var l) && l is > 0 and < int.MaxValue ? (int)l : null;
    }

    // MARK: - Workspaces

    private static async Task<IReadOnlyList<string>> ResolveWorkspacesAsync(IFileSource source, OpenClawInstall install, JsonObject? config,
                                                                             string home, CancellationToken cancellationToken)
    {
        var state = install.StateDir;
        var candidates = new List<string>();
        void Add(JsonNode? node)
        {
            if (Text(node) is { Length: > 0 } path) candidates.Add(ExpandPath(source, path, home, state));
        }
        Add(At(config, "agents", "defaults", "workspace"));
        if (At(config, "agents", "list") is JsonArray agents)
        {
            foreach (var agent in agents) Add(At(agent, "workspace"));
        }
        Add(At(config, "agent", "workspace"));
        Add(At(config, "workspace"));

        var entries = await source.ListAsync(state, cancellationToken).ConfigureAwait(false) ?? [];
        // The state folder's own workspace(s), including per-agent ones (workspace-work).
        foreach (var entry in entries.Where(e => e.IsDirectory && e.Name.StartsWith("workspace", StringComparison.OrdinalIgnoreCase)))
            candidates.Add(entry.FullPath);
        // Clawdbot kept its workspace beside the state folder.
        if (install.Flavor == "clawdbot") candidates.Add(source.Combine(home, "clawd"));
        // A folder that is itself a workspace (pointed at directly).
        if (entries.Any(e => e.IsDirectory && e.Name.Equals("skills", StringComparison.OrdinalIgnoreCase))
            && entries.Any(e => e.Name.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("SOUL.md", StringComparison.OrdinalIgnoreCase)))
            candidates.Add(state);

        var workspaces = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await source.DirectoryExistsAsync(candidate, cancellationToken).ConfigureAwait(false)) workspaces.Add(candidate);
        }
        return workspaces;
    }

    /// <summary>"~/x" → the home folder, a relative path → beside the state folder, an absolute one as is.</summary>
    internal static string ExpandPath(IFileSource source, string raw, string home, string state)
    {
        var path = raw.Trim();
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) return source.Combine(home, path[2..]);
        if (path.StartsWith("$HOME/", StringComparison.Ordinal)) return source.Combine(home, path[6..]);
        var absolute = path.StartsWith('/') || path.StartsWith('\\') || (path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':');
        return absolute ? path : source.Combine(state, path);
    }

    // MARK: - Skills

    private static async Task<IReadOnlyList<OpenClawSkill>> ScanSkillsAsync(IFileSource source, List<(string Dir, string Location)> roots,
                                                                             CancellationToken cancellationToken)
    {
        var found = new List<OpenClawSkill>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, location) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await source.ListAsync(directory, cancellationToken).ConfigureAwait(false) is not { } entries) continue;
            var folders = entries.Where(e => e.IsDirectory && !e.Name.StartsWith('.') && seenFolders.Add(e.FullPath)).Take(300).ToList();
            // Every manifest in one go; the folders without one may be groups holding skills a level down.
            var manifests = await source.ReadManyAsync(folders.Select(f => source.Combine(f.FullPath, "SKILL.md")).ToList(), 256_000, cancellationToken)
                .ConfigureAwait(false);
            for (var i = 0; i < folders.Count; i++)
            {
                if (manifests[i] is { } bytes)
                {
                    found.Add(await BuildSkillAsync(source, folders[i], bytes, location, cancellationToken).ConfigureAwait(false));
                    continue;
                }
                if (await source.ListAsync(folders[i].FullPath, cancellationToken).ConfigureAwait(false) is not { } inner) continue;
                var nested = inner.Where(e => e.IsDirectory && !e.Name.StartsWith('.')).Take(100).ToList();
                var nestedManifests = await source.ReadManyAsync(nested.Select(n => source.Combine(n.FullPath, "SKILL.md")).ToList(), 256_000, cancellationToken)
                    .ConfigureAwait(false);
                for (var j = 0; j < nested.Count; j++)
                {
                    if (nestedManifests[j] is { } nestedBytes)
                        found.Add(await BuildSkillAsync(source, nested[j], nestedBytes, location, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        // OpenClaw's precedence: workspace, then managed, then extra folders. The first of a name wins.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return found.Select(s => s with { Shadowed = !names.Add(s.Name) }).ToList();
    }

    private static async Task<OpenClawSkill> BuildSkillAsync(IFileSource source, FileEntry folder, byte[] manifest, string location,
                                                             CancellationToken cancellationToken)
    {
        var document = SkillDocument.Parse(Encoding.UTF8.GetString(manifest));
        var name = (document["name"] ?? folder.Name).Trim();
        if (name.Length == 0) name = folder.Name;
        var files = await source.ListFilesAsync(folder.FullPath, 4, 300, cancellationToken).ConfigureAwait(false);
        var scripts = files.Any(f => SkillImporter.ScriptExtensions.Contains(System.IO.Path.GetExtension(f).TrimStart('.').ToLowerInvariant()));
        return new OpenClawSkill
        {
            Name = name,
            Description = (document["description"] ?? "").Trim(),
            Directory = folder.FullPath,
            Location = location,
            Files = files,
            HasScripts = scripts,
        };
    }

    // MARK: - Memory files

    private static async Task<IReadOnlyList<OpenClawNoteFile>> ScanNotesAsync(IFileSource source, IReadOnlyList<string> workspaces,
                                                                               CancellationToken cancellationToken)
    {
        var result = new List<OpenClawNoteFile>();
        foreach (var workspace in workspaces)
        {
            if (await source.ListAsync(workspace, cancellationToken).ConfigureAwait(false) is not { } entries) continue;
            var folder = System.IO.Path.GetFileName(workspace.TrimEnd('/', '\\'));
            var label = workspaces.Count > 1 ? folder + "/" : "";

            // Everything to read in this workspace: the named files, then the daily logs, newest first
            // (a year of them is history, not memory — the recent ones are ticked).
            var wanted = new List<(FileEntry Entry, string Name, OpenClawNoteKind Kind, bool Selected)>();
            foreach (var entry in entries.Where(e => !e.IsDirectory))
            {
                var kind = entry.Name.ToLowerInvariant() switch
                {
                    "memory.md" => OpenClawNoteKind.Memory,
                    "user.md" => OpenClawNoteKind.About,
                    "soul.md" or "identity.md" => OpenClawNoteKind.Persona,
                    "agents.md" or "tools.md" => OpenClawNoteKind.Instructions,
                    _ => (OpenClawNoteKind?)null,
                };
                if (kind is not null)
                    wanted.Add((entry, label + entry.Name, kind.Value, kind.Value is OpenClawNoteKind.Memory or OpenClawNoteKind.About));
            }
            var memoryFolder = entries.FirstOrDefault(e => e.IsDirectory && e.Name.Equals("memory", StringComparison.OrdinalIgnoreCase));
            if (memoryFolder is not null && await source.ListAsync(memoryFolder.FullPath, cancellationToken).ConfigureAwait(false) is { } logs)
            {
                var daily = logs.Where(e => !e.IsDirectory && e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => MemoryFiles.DayOf(e.Name) ?? DateTimeOffset.MinValue).ThenByDescending(e => e.Name, StringComparer.Ordinal)
                    .Take(MaxDailyFiles).ToList();
                for (var i = 0; i < daily.Count; i++)
                    wanted.Add((daily[i], label + "memory/" + daily[i].Name, OpenClawNoteKind.Daily, i < DefaultDailyFiles));
            }

            var contents = await source.ReadManyAsync(wanted.Select(w => w.Entry.FullPath).ToList(), MaxNoteBytes, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < wanted.Count; i++)
            {
                if (contents[i] is not { } bytes) continue;
                var text = Encoding.UTF8.GetString(bytes, bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0,
                    bytes.Length - (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));
                if (NoteFile(wanted[i].Entry, text, wanted[i].Name, wanted[i].Kind, wanted[i].Selected) is { } file) result.Add(file);
            }
        }
        return result;
    }

    private static OpenClawNoteFile? NoteFile(FileEntry entry, string text, string relativeName, OpenClawNoteKind kind, bool selected)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var (memoryKind, tags) = kind switch
        {
            OpenClawNoteKind.About => (MemoryKinds.Fact, new[] { "openclaw", "about-user" }),
            OpenClawNoteKind.Persona => (MemoryKinds.Note, new[] { "openclaw", "persona" }),
            OpenClawNoteKind.Instructions => (MemoryKinds.Procedure, new[] { "openclaw", "instructions" }),
            OpenClawNoteKind.Daily => (MemoryKinds.Note, new[] { "openclaw", "daily" }),
            _ => (MemoryKinds.Note, new[] { "openclaw" }),
        };
        var notes = MemoryFiles.Chunk(text, "import:openclaw/" + relativeName, entry.Name, project: null, kind: memoryKind, tags: tags);
        return notes.Count == 0
            ? null
            : new OpenClawNoteFile { Path = entry.FullPath, Name = relativeName, Kind = kind, Notes = notes, SelectedByDefault = selected };
    }

    // MARK: - Credentials

    /// <summary>Gathers secrets, one entry per distinct value, with vault-legal unique names.</summary>
    private sealed class CredentialCollector
    {
        private readonly Dictionary<string, OpenClawCredential> _byValue = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Unresolved { get; } = [];

        public IReadOnlyList<OpenClawCredential> Result =>
            _byValue.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        public bool Has(string value) => _byValue.ContainsKey(value);

        public void Add(string rawName, string value, VaultKind kind, string origin, string description, string? provider, string? url)
        {
            var trimmed = value.Trim();
            if (!LooksLikeSecret(trimmed, kind)) return;
            var wanted = Sanitize(rawName);
            if (_byValue.TryGetValue(trimmed, out var existing))
            {
                if (!existing.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) && !existing.AlsoNamed.Contains(wanted))
                    _byValue[trimmed] = existing with { AlsoNamed = [.. existing.AlsoNamed, wanted] };
                return;
            }
            var name = wanted;
            for (var n = 2; !_names.Add(name); n++)
            {
                // The suffix goes on a name cut short enough to hold it: a long name plus a suffix cut back to the same
                // length would be the same name again, for ever.
                var suffix = "_" + n;
                var head = wanted.Length + suffix.Length > CredentialVault.MaxNameLength ? wanted[..(CredentialVault.MaxNameLength - suffix.Length)] : wanted;
                name = Sanitize(head + suffix);
            }
            _byValue[trimmed] = new OpenClawCredential
            {
                Name = name,
                Secret = new SecretText(trimmed),
                Kind = kind,
                Description = description,
                Origin = origin,
                Provider = provider,
                Url = url,
            };
        }
    }

    [GeneratedRegex(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}$|^\$([A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvReference();

    [GeneratedRegex(@"(?i)(KEY|TOKEN|SECRET|PASSWORD|PASSWD|PASS|PWD|CREDENTIAL|BEARER|PRIVATE)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretEnvName();

    private static readonly HashSet<string> NotSecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "maxTokens", "max_tokens", "tokenLimit", "token_limit", "contextTokens", "tokens", "reserveTokens", "tokenizer", "tokenCount",
        "maxTokenCount", "tokenFile", "keyFile", "passwordFile", "secretFile", "tokenEnv", "apiKeyEnv", "tokenPath",
    };

    internal static bool IsSecretEnvName(string name) => SecretEnvName().IsMatch(name);

    /// <summary>Words in a settings key: "botToken" → bot, token; "api_key" → api, key.</summary>
    private static List<string> KeyWords(string key)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (current.Length > 0) { words.Add(current.ToString().ToLowerInvariant()); current.Clear(); }
                continue;
            }
            if (i > 0 && char.IsUpper(c) && char.IsLower(key[i - 1]) && current.Length > 0)
            {
                words.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }
            current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString().ToLowerInvariant());
        return words;
    }

    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "secret", "token", "password", "passwd", "passphrase", "pwd", "bearer", "credential", "credentials", "apikey",
    };

    /// <summary>A settings key that holds a secret: whole words only, so "secretary" and "tokenizer" don't count.</summary>
    private static bool IsSecretKeyName(string key)
    {
        if (NotSecretNames.Contains(key)) return false;
        var words = KeyWords(key);
        if (words.Count == 0) return false;
        // "tokenFile", "passwordPath", "apiKeyEnv", "maxTokens": these name or bound a secret, they aren't one.
        if (words[^1] is "file" or "path" or "env" or "limit" or "count" or "tokens" or "ttl" or "url" or "id") return false;
        if (words.Any(SecretWords.Contains)) return true;
        for (var i = 0; i + 1 < words.Count; i++)
        {
            if (words[i] is "api" or "private" or "access" or "auth" or "secret" or "signing" && words[i + 1] == "key") return true;
        }
        return false;
    }

    private static VaultKind KindOf(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("passw") || lower.Contains("passphrase") || lower.EndsWith("pwd", StringComparison.Ordinal)) return VaultKind.Password;
        if (lower.Contains("token") || lower.Contains("bearer") || lower.Contains("secret")) return VaultKind.Token;
        return VaultKind.ApiKey;
    }

    /// <summary>A real secret, not a placeholder, a sentence, a number or a reference to somewhere else.</summary>
    internal static bool LooksLikeSecret(string value, VaultKind kind)
    {
        var v = value.Trim();
        if (v.Length < (kind == VaultKind.Password ? 6 : 12)) return false;
        if (kind != VaultKind.Password && v.Any(char.IsWhiteSpace)) return false;
        if (v.All(c => char.IsDigit(c) || c == '.')) return false;
        if (v.StartsWith("${", StringComparison.Ordinal) || v.StartsWith("$(", StringComparison.Ordinal)) return false;
        string[] referencePrefixes = ["env:", "file:", "secret:", "exec:", "op://", "vault:", "keychain:", "http://", "https://", "/", "~"];
        if (referencePrefixes.Any(p => v.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;
        string[] placeholders = ["changeme", "change-me", "your-", "your_", "yourkey", "<", "xxxx", "****", "redacted", "placeholder", "example", "todo", "undefined", "not-needed", "notneeded", "sk-..."];
        var lower = v.ToLowerInvariant();
        if (placeholders.Any(lower.Contains)) return false;
        if (v.Distinct().Count() <= 3) return false;
        // A name or slug ("not-a-secret", "ollama-local"), not a key: real ones mix in digits or capitals.
        // (A passphrase of plain words is a fair password, so this applies to keys and tokens only.)
        if (kind != VaultKind.Password && v.Length < 40 && !v.Any(char.IsDigit) && !(v.Any(char.IsUpper) && v.Any(char.IsLower))) return false;
        return true;
    }

    /// <summary>A name the vault accepts: upper case letters, digits, _ - . only.</summary>
    internal static string Sanitize(string raw)
    {
        var text = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.') text.Append(char.ToUpperInvariant(c));
            else if (text.Length > 0 && text[^1] != '_') text.Append('_');
        }
        var name = text.ToString().Trim('_');
        if (name.Length == 0) name = "OPENCLAW_SECRET";
        return name.Length > CredentialVault.MaxNameLength ? name[..CredentialVault.MaxNameLength] : name;
    }

    /// <summary>"${VAR}" → the value from the env files; anything else is itself. Null when the reference has no value.</summary>
    private static string? Resolve(string value, IReadOnlyDictionary<string, string> env, CredentialCollector collector)
    {
        var match = EnvReference().Match(value.Trim());
        if (!match.Success) return value;
        var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        if (env.TryGetValue(name, out var resolved)) return resolved;
        collector.Unresolved.Add(name);
        return null;
    }

    private static readonly Dictionary<string, string> ConventionalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = "ANTHROPIC_API_KEY", ["openai"] = "OPENAI_API_KEY", ["openrouter"] = "OPENROUTER_API_KEY",
        ["google"] = "GEMINI_API_KEY", ["gemini"] = "GEMINI_API_KEY", ["groq"] = "GROQ_API_KEY", ["mistral"] = "MISTRAL_API_KEY",
        ["xai"] = "XAI_API_KEY", ["deepseek"] = "DEEPSEEK_API_KEY", ["together"] = "TOGETHER_API_KEY", ["fireworks"] = "FIREWORKS_API_KEY",
        ["cerebras"] = "CEREBRAS_API_KEY", ["perplexity"] = "PERPLEXITY_API_KEY", ["moonshot"] = "MOONSHOT_API_KEY", ["zai"] = "ZAI_API_KEY",
        ["minimax"] = "MINIMAX_API_KEY", ["huggingface"] = "HF_TOKEN", ["github"] = "GITHUB_TOKEN", ["brave"] = "BRAVE_API_KEY",
    };

    /// <summary>auth-profiles.json files (one per agent). Returns the OAuth sign-ins it skipped, by provider.</summary>
    private static async Task<List<string>> ReadAuthProfilesAsync(IFileSource source, string state, CredentialCollector collector,
                                                                   CancellationToken cancellationToken)
    {
        var files = new List<string> { source.Combine(state, "auth-profiles.json"), source.Combine(source.Combine(state, "agent"), "auth-profiles.json") };
        if (await source.ListAsync(source.Combine(state, "agents"), cancellationToken).ConfigureAwait(false) is { } agents)
        {
            foreach (var agent in agents.Where(a => a.IsDirectory).Take(40))
                files.Add(source.Combine(source.Combine(agent.FullPath, "agent"), "auth-profiles.json"));
        }
        var oauth = new List<string>();
        foreach (var file in files)
        {
            var text = await source.ReadTextAsync(file, MaxSmallFileBytes, cancellationToken).ConfigureAwait(false);
            if (text is null) continue;
            var root = Json5.TryParse(text, out _);
            var profiles = new List<(string Id, JsonObject Profile)>();
            var container = At(root, "profiles") ?? root;
            if (container is JsonObject map)
            {
                foreach (var (id, node) in map)
                {
                    if (node is JsonObject profile && (profile["type"] is not null || profile["provider"] is not null || profile["key"] is not null || profile["token"] is not null))
                        profiles.Add((id, profile));
                }
            }
            else if (container is JsonArray list)
            {
                foreach (var node in list)
                {
                    if (node is JsonObject profile) profiles.Add((Text(profile["id"]) ?? Text(profile["profileId"]) ?? "profile", profile));
                }
            }
            foreach (var (id, profile) in profiles)
            {
                var provider = Text(profile["provider"]) ?? id.Split(':')[0];
                var profileName = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : "";
                var type = (Text(profile["type"]) ?? (profile["token"] is not null ? "token" : "api_key")).ToLowerInvariant();
                if (type.Contains("oauth"))
                {
                    oauth.Add(provider);
                    continue;
                }
                var secret = type == "token" ? Text(profile["token"]) ?? Text(profile["key"]) : Text(profile["key"]) ?? Text(profile["apiKey"]) ?? Text(profile["api_key"]) ?? Text(profile["token"]);
                if (secret is null) continue;
                var isDefault = profileName.Length == 0 || profileName.Equals("default", StringComparison.OrdinalIgnoreCase);
                var suffix = type == "token" ? "TOKEN" : "API_KEY";
                var name = isDefault && ConventionalNames.TryGetValue(provider, out var conventional) && type != "token"
                    ? conventional
                    : $"OPENCLAW_{Sanitize(provider)}{(isDefault ? "" : "_" + Sanitize(profileName))}_{suffix}";
                collector.Add(name, secret, type == "token" ? VaultKind.Token : VaultKind.ApiKey,
                    $"auth-profiles.json ({id})", $"OpenClaw auth profile “{id}” for {provider}.", provider, null);
            }
        }
        return oauth;
    }

    /// <summary>KEY=value lines from OpenClaw's own .env files; also fills <paramref name="env"/> so ${VAR} references resolve.</summary>
    private static async Task ReadEnvFilesAsync(IFileSource source, string state, IReadOnlyList<string> workspaces,
                                                Dictionary<string, string> env, CancellationToken cancellationToken)
    {
        var files = new List<string> { source.Combine(state, ".env") };
        files.AddRange(workspaces.Select(w => source.Combine(w, ".env")));
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var text = await source.ReadTextAsync(file, 256_000, cancellationToken).ConfigureAwait(false);
            if (text is null) continue;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                var name = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
                else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment and > 0) value = value[..comment].TrimEnd();
                if (name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && value.Length > 0) env[name] = value;
            }
        }
    }

    /// <summary>Walk the config for anything that looks like a key, token or password.</summary>
    private static void SweepConfig(JsonObject? config, Dictionary<string, string> env, CredentialCollector collector,
                                    IReadOnlyList<OpenClawModelServer> servers)
    {
        if (config is null) return;
        // The env blocks: their entries are environment variables, named as such.
        void EnvBlock(JsonNode? node, string origin)
        {
            if (node is not JsonObject block) return;
            foreach (var (name, value) in block)
            {
                if (value is JsonObject inner && name is "vars") { EnvBlock(inner, origin); continue; }
                if (Text(value) is { } text && Resolve(text, env, collector) is { } resolved)
                {
                    env.TryAdd(name, resolved);
                    if (IsSecretEnvName(name)) collector.Add(name, resolved, KindOf(name), origin, $"OpenClaw environment setting {name}.", null, null);
                }
            }
        }
        EnvBlock(At(config, "env"), "openclaw.json env");
        if (At(config, "skills", "entries") is JsonObject entries)
        {
            foreach (var (skill, entry) in entries)
                EnvBlock(At(entry, "env"), $"openclaw.json skills.entries.{skill}.env");
        }
        // The apiKey of a provider that was turned into a model server travels with the server.
        var handled = new HashSet<string>(servers.Where(s => s.Compatible).Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        var path = new List<string>();
        void Walk(JsonNode? node)
        {
            if (path.Count > 12) return;
            if (node is JsonObject obj)
            {
                // A reference to a secret kept elsewhere ({ source: "env", id: "…" }), not a secret.
                if (Text(obj["source"]) is "env" or "file" or "exec" && obj["id"] is not null) return;
                foreach (var (key, child) in obj)
                {
                    path.Add(key);
                    var inEnv = path.Count >= 1 && (path[0] == "env" || (path.Count >= 4 && path[0] == "skills" && path[1] == "entries" && path[3] == "env"));
                    var providerKey = path.Count == 4 && path[0] == "models" && path[1] == "providers" && handled.Contains(path[2]) && key == "apiKey";
                    if (!inEnv && !providerKey)
                    {
                        if (Text(child) is { } text && IsSecretKeyName(key)) AddFromConfig(path, key, text);
                        else if (child is JsonObject or JsonArray) Walk(child);
                    }
                    path.RemoveAt(path.Count - 1);
                }
            }
            else if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count && i < 100; i++)
                {
                    path.Add(i.ToString());
                    Walk(array[i]);
                    path.RemoveAt(path.Count - 1);
                }
            }
        }

        void AddFromConfig(List<string> where, string key, string text)
        {
            var resolved = Resolve(text, env, collector);
            if (resolved is null) return;
            var provider = where.Count >= 3 && where[0] is "models" && where[1] is "providers" ? where[2]
                : where.Count >= 2 && where[0] is "channels" ? where[1]
                : where.Count >= 3 && where[0] is "skills" && where[1] is "entries" ? where[2] : null;
            var dotted = string.Join(".", where);
            var name = dotted.Equals("gateway.auth.token", StringComparison.OrdinalIgnoreCase) ? "OPENCLAW_GATEWAY_TOKEN"
                : dotted.Equals("gateway.auth.password", StringComparison.OrdinalIgnoreCase) ? "OPENCLAW_GATEWAY_PASSWORD"
                : "OPENCLAW_" + string.Join("_", where);
            collector.Add(name, resolved, KindOf(key), $"openclaw.json {dotted}", $"OpenClaw setting {dotted}.", provider, null);
        }

        Walk(config);
    }

    // MARK: - Model servers

    private static IReadOnlyList<OpenClawModelServer> ReadModelServers(JsonObject? config, IFileSource source, CredentialCollector collector,
                                                                         IReadOnlyDictionary<string, string> env)
    {
        if (At(config, "models", "providers") is not JsonObject providers) return [];
        var primary = Text(At(config, "agents", "defaults", "model", "primary")) ?? Text(At(config, "agents", "defaults", "model"));
        string? primaryProvider = null;
        string? primaryModel = null;
        if (primary is not null && primary.Contains('/'))
        {
            primaryProvider = primary[..primary.IndexOf('/')];
            primaryModel = primary[(primary.IndexOf('/') + 1)..];
        }

        var result = new List<OpenClawModelServer>();
        foreach (var (id, node) in providers)
        {
            if (node is not JsonObject provider) continue;
            var baseUrl = Text(provider["baseUrl"])?.Trim();
            if (string.IsNullOrEmpty(baseUrl)) continue;
            var api = Text(provider["api"])?.Trim();
            var compatible = api is null || api.Equals("openai-completions", StringComparison.OrdinalIgnoreCase) || api.Equals("openai", StringComparison.OrdinalIgnoreCase);

            // "localhost" on the machine OpenClaw runs on means that machine, not this one.
            string? note = compatible ? null : $"speaks the “{api}” protocol, which DSH doesn't";
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var written) && (written.UserInfo.Length > 0 || written.Query.Length > 1))
                note = (note is null ? "" : note + "; ") + "its address carries a sign-in or token — check it before bringing it in";
            if (source.NetworkHost is { Length: > 0 } host && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                && (uri.IsLoopback || uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var builder = new UriBuilder(uri) { Host = host };
                    baseUrl = builder.Uri.ToString().TrimEnd('/');
                    note = (note is null ? "" : note + "; ") + $"its address was localhost on {host}, so it now points at {host}"
                           + (uri.Scheme == Uri.UriSchemeHttp ? " over plain http, so anything sent to it (its key too) crosses the network unencrypted" : "");
                }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException)
                {
                    note = (note is null ? "" : note + "; ") + $"its address was localhost on {host}, and couldn't be pointed there";
                }
            }

            var models = new List<string>();
            int? window = null;
            if (provider["models"] is JsonArray list)
            {
                foreach (var m in list)
                {
                    var modelId = Text(m) ?? Text(At(m, "id"));
                    if (string.IsNullOrWhiteSpace(modelId)) continue;
                    models.Add(modelId);
                    window ??= Number(At(m, "contextWindow"));
                }
            }
            SecretText? key = null;
            if (Text(provider["apiKey"]) is { Length: > 0 } rawKey && Resolve(rawKey, env, collector) is { } resolved && resolved.Length > 0
                && !resolved.StartsWith("${", StringComparison.Ordinal))
                key = new SecretText(resolved);

            result.Add(new OpenClawModelServer
            {
                Id = id,
                BaseUrl = baseUrl,
                Api = api,
                Models = models,
                ContextWindow = window,
                ApiKey = key,
                Compatible = compatible,
                Note = note,
                DefaultModel = primaryProvider is not null && id.Equals(primaryProvider, StringComparison.OrdinalIgnoreCase) ? primaryModel : null,
            });
        }
        return result;
    }

    // MARK: - What isn't imported

    private static async Task<int> CountCronJobsAsync(IFileSource source, string state, CancellationToken cancellationToken)
    {
        var text = await source.ReadTextAsync(source.Combine(source.Combine(state, "cron"), "jobs.json"), MaxSmallFileBytes, cancellationToken).ConfigureAwait(false);
        if (text is null) return 0;
        var root = Json5.TryParse(text, out _);
        return root is JsonArray array ? array.Count : At(root, "jobs") is JsonArray jobs ? jobs.Count : root is JsonObject obj ? obj.Count(p => p.Value is JsonObject) : 0;
    }

    private static async Task<int> CountSessionsAsync(IFileSource source, string state, CancellationToken cancellationToken)
    {
        var total = 0;
        if (await source.ListAsync(source.Combine(state, "agents"), cancellationToken).ConfigureAwait(false) is not { } agents) return 0;
        foreach (var agent in agents.Where(a => a.IsDirectory).Take(40))
        {
            var sessions = await source.ListAsync(source.Combine(agent.FullPath, "sessions"), cancellationToken).ConfigureAwait(false);
            total += sessions?.Count(e => !e.IsDirectory && e.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) ?? 0;
        }
        return total;
    }
}
