using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Sending Vision's things to OpenClaw on this PC
//
// The other direction of Settings › Import & Export. What the person ticks goes to the OpenClaw they picked:
//   skills         → <state>\skills (OpenClaw's managed skills; a workspace skill of the same name still wins there)
//   memory         → the workspace's MEMORY.md, as one marked "From Vision" block — exporting again updates it in place,
//                    and notes Vision brought in from OpenClaw don't go back
//   keys/passwords → <state>\.env as NAME="value" lines — only after the person confirmed it in a dialog
//   model servers  → openclaw.json models.providers (their keys as ${NAME} references into .env when keys go too); a
//                    config with comments is never rewritten: the entries go to vision-model-servers.json5 beside it
// Nothing is logged with a value in it; results name things, never show them. Vision for macOS
// (OpenClawTransfer.swift) writes the same blocks, names and files.

/// <summary>What happens when the other side already has something of that name.</summary>
public enum OpenClawConflict
{
    /// <summary>Keep both: the incoming one gets a new name.</summary>
    KeepBoth,
    /// <summary>The incoming one replaces what is there.</summary>
    Replace,
    /// <summary>Leave what is there alone.</summary>
    Skip,
}

public static class OpenClawConflicts
{
    public static IReadOnlyList<OpenClawConflict> All { get; } = [OpenClawConflict.KeepBoth, OpenClawConflict.Replace, OpenClawConflict.Skip];

    /// <summary>The Mac's words.</summary>
    public static string Label(this OpenClawConflict conflict) => conflict switch
    {
        OpenClawConflict.KeepBoth => "Keep both (rename the new one)",
        OpenClawConflict.Replace => "Replace what is there",
        _ => "Skip it",
    };

    public static ConflictPolicy Policy(this OpenClawConflict conflict) =>
        conflict == OpenClawConflict.Replace ? ConflictPolicy.Replace : ConflictPolicy.Rename;
}

/// <summary>A Vision model route, as it would go into OpenClaw's config.</summary>
public sealed record OpenClawExportServer(string Name, string BaseUrl, string Model, int? ContextWindow, string? ApiKey)
{
    /// <summary>The provider id it gets in openclaw.json: "vision-" + a slug of the route's name.</summary>
    public string ProviderId => "vision-" + (SkillNaming.Slug(Name) is { Length: > 0 } slug ? slug : "server");

    /// <summary>The .env name its key goes under: VISION_&lt;NAME&gt;_API_KEY.</summary>
    public string KeyName => OpenClawReader.Sanitize(ProviderId).Replace('-', '_').Replace('.', '_') + "_API_KEY";

    public override string ToString() => $"OpenClawExportServer {{ {Name} · {Model} · {BaseUrl} }}";
}

public sealed class OpenClawExportSelection
{
    public IReadOnlyList<Skill> Skills { get; init; } = [];
    /// <summary>Send the memory notes.</summary>
    public bool Memory { get; init; }
    /// <summary>Vault entries to send, by name. Written only when <see cref="VaultConfirmed"/>.</summary>
    public IReadOnlySet<string> Vault { get; init; } = new HashSet<string>();
    /// <summary>The person confirmed writing secrets in plain text to OpenClaw's .env.</summary>
    public bool VaultConfirmed { get; init; }
    public IReadOnlyList<OpenClawExportServer> Servers { get; init; } = [];
    public OpenClawConflict Conflict { get; init; } = OpenClawConflict.Skip;
}

public sealed record OpenClawExportResult
{
    public List<string> Skills { get; } = [];
    public Dictionary<string, string> SkillsSkipped { get; } = new(StringComparer.Ordinal);
    public int MemoryAdded { get; set; }
    public int MemoryUpdated { get; set; }
    public int MemoryUnchanged { get; set; }
    /// <summary>How many notes went into the block.</summary>
    public int Notes { get; set; }
    /// <summary>The .env names written (never the values).</summary>
    public List<string> Secrets { get; } = [];
    public Dictionary<string, string> SecretsSkipped { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Provider ids added to (or replaced in) openclaw.json.</summary>
    public List<string> Servers { get; } = [];
    public Dictionary<string, string> ServersSkipped { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where the model servers were written instead, when openclaw.json couldn't be edited safely.</summary>
    public string? ServersSnippet { get; set; }
    public List<string> Problems { get; } = [];

    /// <summary>"2 skills, 1 memory file, 3 keys and passwords and 1 model server".</summary>
    public string Summary
    {
        get
        {
            static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
            var parts = new List<string>();
            if (Skills.Count > 0) parts.Add(N(Skills.Count, "skill", "skills"));
            if (MemoryAdded + MemoryUpdated > 0) parts.Add(N(Notes, "memory note", "memory notes"));
            if (Secrets.Count > 0) parts.Add(N(Secrets.Count, "key or password", "keys and passwords"));
            if (Servers.Count > 0) parts.Add(N(Servers.Count, "model server", "model servers"));
            return parts.Count switch
            {
                0 => "nothing new",
                1 => parts[0],
                _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
            };
        }
    }
}

/// <summary>Blocks of a Markdown file that one importer owns, between HTML-comment markers carrying the block's name
/// and a fingerprint of its contents: the same block again is a no-op, a changed one is replaced in place. (The Mac's
/// MarkdownBlocks, the same markers.)</summary>
public static class MarkdownBlocks
{
    public enum Outcome { Added, Updated, Unchanged }

    public static string Begin(string tag, string name, string hash) => $"<!-- {tag}:begin {name} {hash} -->";
    public static string End(string tag, string name) => $"<!-- {tag}:end {name} -->";

    public static (string Text, Outcome Outcome) Upsert(string tag, string name, string title, string body, string text)
    {
        var hash = CredentialVault.Fingerprint(body);
        var block = Begin(tag, name, hash) + "\n## " + title + "\n\n" + body.Trim() + "\n" + End(tag, name);
        var beginPrefix = $"<!-- {tag}:begin {name} ";
        var start = text.IndexOf(beginPrefix, StringComparison.Ordinal);
        if (start >= 0)
        {
            var lineEnd = text.IndexOf("-->", start, StringComparison.Ordinal);
            lineEnd = lineEnd < 0 ? start + beginPrefix.Length : lineEnd + 3;
            if (text[start..lineEnd].Contains(hash, StringComparison.Ordinal)) return (text, Outcome.Unchanged);
            var close = text.IndexOf(End(tag, name), lineEnd, StringComparison.Ordinal);
            if (close >= 0)
                return (text[..start] + block + text[(close + End(tag, name).Length)..], Outcome.Updated);
        }
        var output = text;
        if (output.Length > 0)
        {
            while (output.EndsWith("\n\n\n", StringComparison.Ordinal)) output = output[..^1];
            output += output.EndsWith('\n') ? "\n" : "\n\n";
        }
        return (output + block + "\n", Outcome.Added);
    }

    /// <summary>The text with every block of <paramref name="tag"/> taken out.</summary>
    public static string Strip(string tag, string text)
    {
        var output = text;
        while (true)
        {
            var start = output.IndexOf($"<!-- {tag}:begin ", StringComparison.Ordinal);
            if (start < 0) return output;
            var lineEnd = output.IndexOf("-->", start, StringComparison.Ordinal);
            if (lineEnd < 0) return output;
            var header = output[(start + $"<!-- {tag}:begin ".Length)..lineEnd].Trim();
            var name = string.Join(' ', header.Split(' ').SkipLast(1));
            var end = End(tag, name);
            var close = output.IndexOf(end, lineEnd, StringComparison.Ordinal);
            if (close < 0) return output;
            output = output[..start] + output[(close + end.Length)..];
        }
    }
}

public static class OpenClawExporter
{
    /// <summary>The marker Vision's exported blocks carry (the Mac's too).</summary>
    public const string BlockTag = "vision-export";
    /// <summary>Where model servers go when openclaw.json can't be edited without losing its comments.</summary>
    public const string ServersSnippetName = "vision-model-servers.json5";

    /// <summary>The workspace memory goes to: the install's first workspace, else &lt;state&gt;\workspace.</summary>
    public static async Task<string> WorkspaceAsync(OpenClawInstall install, IFileSource? source = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var bundle = await OpenClawReader.ReadAsync(source ?? new LocalFileSource(), install, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (bundle.Workspaces.FirstOrDefault() is { Length: > 0 } first) return first;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Fall back to the default workspace.
        }
        return Path.Combine(install.StateDir, "workspace");
    }

    /// <summary>Send what was chosen to <paramref name="install"/> (on this PC). Vault secrets are written only when the
    /// selection says the person confirmed it; otherwise they are listed as skipped.</summary>
    public static OpenClawExportResult Export(OpenClawExportSelection selection, OpenClawInstall install, string workspace,
                                              MemoryStore? memory, CredentialVault? vault)
    {
        var result = new OpenClawExportResult();
        var state = install.StateDir;
        ExportSkills(selection, Path.Combine(state, "skills"), result);
        if (selection.Memory) ExportMemory(memory, Path.Combine(workspace, "MEMORY.md"), result);
        var envNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (selection.Vault.Count > 0 || selection.Servers.Any(s => s.ApiKey is { Length: > 0 }))
        {
            var secrets = new List<(string Name, string Value, string Label)>();
            if (selection.Vault.Count > 0)
            {
                if (!selection.VaultConfirmed || vault is null)
                {
                    foreach (var name in selection.Vault) result.SecretsSkipped[name] = "Not confirmed — keys and passwords are only written after you confirm.";
                }
                else
                {
                    foreach (var name in selection.Vault.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        string? value = null;
                        try
                        {
                            value = vault.Entry(name) is { } entry ? vault.Value(entry.Id) : null;
                        }
                        catch (VaultException)
                        {
                            // Reported below as not in the vault.
                        }
                        if (string.IsNullOrEmpty(value)) result.SecretsSkipped[name] = "It isn't in the vault any more.";
                        else secrets.Add((EnvName(name), value, name));
                    }
                }
            }
            if (selection.VaultConfirmed)
                foreach (var server in selection.Servers.Where(s => s.ApiKey is { Length: > 0 }))
                    secrets.Add((server.KeyName, server.ApiKey!, server.KeyName));
            if (secrets.Count > 0) WriteSecrets(secrets, Path.Combine(state, ".env"), selection.Conflict, result, envNames);
        }
        if (selection.Servers.Count > 0) ExportServers(selection, install, envNames, result);
        return result;
    }

    // MARK: Skills

    private static void ExportSkills(OpenClawExportSelection selection, string root, OpenClawExportResult result)
    {
        foreach (var skill in selection.Skills)
        {
            var slug = SkillNaming.Slug(skill.Name) is { Length: > 0 } s ? s : "skill";
            if (selection.Conflict == OpenClawConflict.Skip && Directory.Exists(Path.Combine(root, slug)))
            {
                result.SkillsSkipped[skill.Name] = $"OpenClaw already has a skill named {slug}; kept that one.";
                continue;
            }
            try
            {
                SkillExporter.Write([skill], ExportFormat.Portable, root, selection.Conflict.Policy());
                result.Skills.Add(skill.Name);
            }
            catch (Exception error) when (error is SkillException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                result.SkillsSkipped[skill.Name] = error.Message;
            }
        }
    }

    // MARK: Memory

    /// <summary>The notes Vision sends: its own (none it brought in from OpenClaw), newest first, without any that look like a
    /// key or a password.</summary>
    public static (string Body, int Count, int LeftOut) MemoryBody(MemoryStore memory)
    {
        var builder = new StringBuilder();
        var count = 0;
        var leftOut = 0;
        foreach (var note in memory.All().Where(n => !n.Source.StartsWith("import:openclaw", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(n => n.Pinned).ThenByDescending(n => n.UpdatedAt))
        {
            if (SecretGuard.LooksLikeSecret(note.Title + "\n" + note.Body))
            {
                leftOut++;
                continue;
            }
            builder.Append("### ").Append(note.Title.Trim()).Append('\n').Append('\n');
            builder.Append(note.Body.Trim()).Append('\n');
            var facts = new List<string> { note.Kind };
            if (note.Pinned) facts.Add("pinned");
            if (note.Tags.Count > 0) facts.Add(string.Join(", ", note.Tags));
            if (note.Project is { } project) facts.Add("project " + Path.GetFileName(project.TrimEnd('\\', '/')));
            facts.Add(note.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            builder.Append('\n').Append('_').Append(string.Join(" · ", facts)).Append('_').Append('\n').Append('\n');
            count++;
        }
        return (builder.ToString().Trim(), count, leftOut);
    }

    private static void ExportMemory(MemoryStore? memory, string target, OpenClawExportResult result)
    {
        if (memory is null)
        {
            result.Problems.Add("Vision's memory couldn't be read.");
            return;
        }
        var (body, count, leftOut) = MemoryBody(memory);
        if (leftOut > 0)
            result.Problems.Add($"{leftOut} memory note{(leftOut == 1 ? " was" : "s were")} left out because {(leftOut == 1 ? "it looks" : "they look")} like a key or a password.");
        if (count == 0)
        {
            result.Problems.Add("Vision has no memory notes of its own to send yet.");
            return;
        }
        try
        {
            var existing = File.Exists(target) ? File.ReadAllText(target) : "";
            var (text, outcome) = MarkdownBlocks.Upsert(BlockTag, "MEMORY.md", "From Vision — MEMORY.md", body, existing);
            switch (outcome)
            {
                case MarkdownBlocks.Outcome.Added: result.MemoryAdded++; break;
                case MarkdownBlocks.Outcome.Updated: result.MemoryUpdated++; break;
                default: result.MemoryUnchanged++; break;
            }
            result.Notes = count;
            if (outcome != MarkdownBlocks.Outcome.Unchanged) WriteAtomic(target, text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.Problems.Add($"MEMORY.md couldn't be written: {error.Message}");
        }
    }

    // MARK: Keys and passwords

    /// <summary>A vault name as an environment variable: upper case, letters, digits and _.</summary>
    public static string EnvName(string vaultName) => OpenClawReader.Sanitize(vaultName).Replace('-', '_').Replace('.', '_');

    /// <summary>Upsert NAME="value" lines in an .env file. Values are never logged; the result names only.
    /// <paramref name="written"/> hears, for each label, the name it landed under.</summary>
    internal static void WriteSecrets(IReadOnlyList<(string Name, string Value, string Label)> secrets, string file, OpenClawConflict conflict,
                                      OpenClawExportResult result, Dictionary<string, string> written)
    {
        var lines = (File.Exists(file) ? File.ReadAllText(file) : "").Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var existing = new Dictionary<string, (int Index, string Value)>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
            if (ParseEnvLine(lines[i]) is { } pair) existing[pair.Name] = (i, pair.Value);
        var changed = false;
        foreach (var (name, value, label) in secrets)
        {
            var line = $"{name}={Quoted(value)}";
            if (existing.TryGetValue(name, out var found))
            {
                if (found.Value == value)
                {
                    result.SecretsSkipped[label] = $"OpenClaw already has {name} with this value.";
                    written[label] = name;
                    continue;
                }
                switch (conflict)
                {
                    case OpenClawConflict.Skip:
                        result.SecretsSkipped[label] = $"OpenClaw already has {name} with a different value; kept that one.";
                        continue;
                    case OpenClawConflict.Replace:
                        lines[found.Index] = line;
                        existing[name] = (found.Index, value);
                        break;
                    default:
                        var candidate = name + "_VISION";
                        for (var n = 2; existing.ContainsKey(candidate); n++) candidate = $"{name}_VISION_{n}";
                        lines.Add($"{candidate}={Quoted(value)}");
                        existing[candidate] = (lines.Count - 1, value);
                        result.Secrets.Add(candidate);
                        written[label] = candidate;
                        changed = true;
                        continue;
                }
            }
            else
            {
                lines.Add(line);
                existing[name] = (lines.Count - 1, value);
            }
            result.Secrets.Add(name);
            written[label] = name;
            changed = true;
        }
        if (!changed) return;
        try
        {
            WriteAtomic(file, string.Join("\n", lines) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.Problems.Add($"OpenClaw's .env couldn't be written: {error.Message}");
            result.Secrets.Clear();
            written.Clear();
        }
    }

    /// <summary>NAME=value (optionally "export ", quoted or not), or null for a comment or a blank line.</summary>
    internal static (string Name, string Value)? ParseEnvLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#')) return null;
        if (text.StartsWith("export ", StringComparison.Ordinal)) text = text[7..].TrimStart();
        var eq = text.IndexOf('=');
        if (eq <= 0) return null;
        var name = text[..eq].Trim();
        var value = text[(eq + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = Unescape(value[1..^1]);
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1];
        return (name, value);
    }

    private static string Unescape(string quoted)
    {
        var output = new StringBuilder(quoted.Length);
        for (var i = 0; i < quoted.Length; i++)
        {
            if (quoted[i] != '\\' || i + 1 == quoted.Length)
            {
                output.Append(quoted[i]);
                continue;
            }
            var next = quoted[++i];
            output.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
        }
        return output.ToString();
    }

    /// <summary>A dotenv value in double quotes, with backslashes, quotes and line breaks escaped.</summary>
    internal static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    // MARK: Model servers

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonObject ProviderEntry(OpenClawExportServer server, IReadOnlyDictionary<string, string> envNames)
    {
        var model = new JsonObject { ["id"] = server.Model, ["name"] = server.Model };
        if (server.ContextWindow is { } window) model["contextWindow"] = window;
        var entry = new JsonObject { ["baseUrl"] = server.BaseUrl, ["api"] = "openai-completions" };
        if (envNames.TryGetValue(server.KeyName, out var envName)) entry["apiKey"] = "${" + envName + "}";
        entry["models"] = new JsonArray(model);
        return entry;
    }

    private static void ExportServers(OpenClawExportSelection selection, OpenClawInstall install, IReadOnlyDictionary<string, string> envNames,
                                      OpenClawExportResult result)
    {
        var configPath = install.ConfigPath ?? Path.Combine(install.StateDir, "openclaw.json");
        JsonObject? config = null;
        var editable = true;
        if (File.Exists(configPath))
        {
            try
            {
                // Strict JSON only: a config with comments or trailing commas would lose them if rewritten.
                config = JsonNode.Parse(File.ReadAllText(configPath), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                }) as JsonObject;
                editable = config is not null;
            }
            catch (JsonException)
            {
                editable = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                result.Problems.Add($"{Path.GetFileName(configPath)} couldn't be read: {error.Message}");
                editable = false;
            }
        }
        config ??= [];
        var models = config["models"] as JsonObject;
        if (models is null && editable) config["models"] = models = [];
        var providers = models?["providers"] as JsonObject;
        if (providers is null && models is not null) models["providers"] = providers = [];
        var snippet = new JsonObject();
        foreach (var server in selection.Servers)
        {
            if (!server.BaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                result.ServersSkipped[server.Name] = "OpenClaw needs an http(s) address for it.";
                continue;
            }
            var id = server.ProviderId;
            if (providers is not null && providers.ContainsKey(id))
            {
                if (selection.Conflict == OpenClawConflict.Skip)
                {
                    result.ServersSkipped[server.Name] = $"OpenClaw already has a model server named {id}; kept that one.";
                    continue;
                }
                if (selection.Conflict == OpenClawConflict.KeepBoth)
                {
                    var candidate = id + "-2";
                    for (var n = 3; providers.ContainsKey(candidate); n++) candidate = $"{id}-{n}";
                    id = candidate;
                }
            }
            var entry = ProviderEntry(server, envNames);
            if (editable && providers is not null) providers[id] = entry;
            else snippet[id] = entry;
            result.Servers.Add(id);
        }
        if (result.Servers.Count == 0) return;
        try
        {
            if (editable)
            {
                if (File.Exists(configPath)) File.Copy(configPath, configPath + ".vision-backup", overwrite: true);
                WriteAtomic(configPath, config.ToJsonString(Indented) + "\n");
            }
            else
            {
                var path = Path.Combine(install.StateDir, ServersSnippetName);
                var text = "// Model servers from Vision. openclaw.json has comments, so Vision didn't rewrite it:\n"
                           + "// copy these entries under models.providers in openclaw.json.\n"
                           + new JsonObject { ["models"] = new JsonObject { ["providers"] = snippet } }.ToJsonString(Indented) + "\n";
                WriteAtomic(path, text);
                result.ServersSnippet = path;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.Problems.Add($"The model servers couldn't be written: {error.Message}");
            result.Servers.Clear();
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.vision-{Guid.NewGuid().ToString("N")[..8]}";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
