using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - What goes up to Guardian, and comes back down
//
// Chats travel in the transcript shape Vision for macOS 0.15.1 exports (GuardianSettings.chatJSON): {id, title, cwd,
// rows:[{kind, text, toolName, argSummary, output, isError, at}]}, with `at` in Apple's reference-date seconds (what
// Swift's default JSONEncoder writes) — so a chat exported from either harness imports on the other. Code projects go
// up as a text manifest; skills as their files.

/// <summary>One chat as Guardian stores it.</summary>
public sealed record GuardianChatTranscript(string Id, string Title, string? Cwd, IReadOnlyList<LogItemRow> Rows);

public static class GuardianChats
{
    /// <summary>2001-01-01T00:00:00Z: Foundation's reference date, the zero of the Mac's dates.</summary>
    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The kinds both harnesses store; anything else in a payload is skipped.</summary>
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "user", "assistant", "tool", "notice", "error", "compaction" };

    /// <summary>A chat's transcript as the payload Guardian keeps; an imported chat also says where it came from
    /// (<c>source</c>, <c>sourceId</c> — as Vision for macOS sends it).</summary>
    public static string ToPayload(string id, string title, string? cwd, IEnumerable<LogItemRow> rows, ChatOrigin? origin = null)
    {
        var list = new JsonArray();
        foreach (var row in rows)
        {
            var item = new JsonObject { ["kind"] = row.Kind };
            if (row.Text is not null) item["text"] = row.Text;
            if (row.ToolName is not null) item["toolName"] = row.ToolName;
            if (row.ArgSummary is not null) item["argSummary"] = row.ArgSummary;
            if (row.Output is not null) item["output"] = row.Output;
            item["isError"] = row.IsError;
            item["at"] = Math.Round((row.At - AppleEpoch).TotalSeconds, 3);
            list.Add(item);
        }
        var chat = new JsonObject { ["id"] = id, ["title"] = title };
        if (cwd is not null) chat["cwd"] = cwd;
        chat["rows"] = list;
        chat["harness"] = GuardianClient.Source;
        if (origin is { Source.Length: > 0, Id.Length: > 0 })
        {
            chat["source"] = origin.Source;
            chat["sourceId"] = origin.Id;
        }
        return chat.ToJsonString();
    }

    /// <summary>A payload back as a transcript (null when it isn't one). Dates may be Apple reference seconds, ISO text or
    /// epoch milliseconds; the Mac's plan rows ("todos") come back as notices.</summary>
    public static GuardianChatTranscript? FromPayload(string payload, string? fallbackId = null)
    {
        JsonObject? chat;
        try
        {
            chat = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (chat?["rows"] is not JsonArray rows) return null;
        var id = GuardianJson.Text(chat["id"]) ?? fallbackId;
        if (string.IsNullOrWhiteSpace(id) || !IsSafeId(id)) return null;
        var output = new List<LogItemRow>();
        foreach (var node in rows.OfType<JsonObject>())
        {
            var kind = GuardianJson.Text(node["kind"]) ?? "";
            var text = GuardianJson.Text(node["text"]);
            if (kind == "todos")
            {
                kind = "notice";
                text = "Plan:\n" + text;
            }
            if (!Kinds.Contains(kind)) continue;
            output.Add(new LogItemRow(id, output.Count, kind, text, GuardianJson.Text(node["toolName"]), GuardianJson.Text(node["argSummary"]),
                GuardianJson.Text(node["output"]), GuardianJson.Bool(node["isError"]) == true, ReadDate(node["at"]) ?? DateTimeOffset.Now));
        }
        return new GuardianChatTranscript(id, GuardianJson.Text(chat["title"]) ?? "Chat from Guardian", GuardianJson.Text(chat["cwd"]), output);
    }

    /// <summary>A number is Apple reference seconds (the Mac's), unless it is too big to be (epoch milliseconds).</summary>
    private static DateTimeOffset? ReadDate(JsonNode? node)
    {
        if (node is JsonValue value && !value.TryGetValue<string>(out _) && GuardianJson.Number(node) is { } seconds)
        {
            if (seconds > 100_000_000_000) return DateTimeOffset.FromUnixTimeMilliseconds((long)seconds);
            try
            {
                return AppleEpoch.AddSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return GuardianJson.Date(node);
    }

    /// <summary>An id fit to be a file name in the conversations folder (letters, digits, - and _).</summary>
    public static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 120 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public static class GuardianProjects
{
    /// <summary>Folders never uploaded: build output, dependencies, version control (the Mac's list, plus .NET's bin and obj).</summary>
    public static readonly IReadOnlyList<string> SkippedFolders = ["node_modules", ".git", ".build", "dist", ".venv", "__pycache__", ".next", "bin", "obj"];

    /// <summary>Files that hold keys or passwords: never uploaded, wherever they are.</summary>
    public static bool IsCredentialFile(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is ".env" or ".netrc" or ".npmrc" or ".pypirc" or ".git-credentials" or "credentials" or "credentials.json"
            or "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519") return true;
        if (lower.StartsWith(".env.", StringComparison.Ordinal) && lower is not (".env.example" or ".env.sample" or ".env.template")) return true;
        return lower.EndsWith(".pem", StringComparison.Ordinal) || lower.EndsWith(".key", StringComparison.Ordinal)
            || lower.EndsWith(".pfx", StringComparison.Ordinal) || lower.EndsWith(".p12", StringComparison.Ordinal);
    }

    public const int MaxFileBytes = 400_000;
    public const int MaxFiles = 2_000;

    /// <summary>What a project upload holds and what it left out.</summary>
    public sealed record Manifest(IReadOnlyDictionary<string, string> Files, int SkippedBinary, int SkippedLarge, int SkippedSecret, bool Truncated)
    {
        public long Bytes => Files.Sum(f => (long)Encoding.UTF8.GetByteCount(f.Value));
    }

    /// <summary>The project's text files by "/"-separated relative path (UTF-8 text only, under 400 KB each, at most
    /// 2,000), skipping build output, dependencies and files that hold credentials.</summary>
    public static Manifest Collect(string root, CancellationToken cancellationToken = default)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int binary = 0, large = 0, secret = 0;
        var truncated = false;
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                // Links could lead out of the project: never followed.
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) pending.Push(entry);
                    continue;
                }
                if (IsCredentialFile(name))
                {
                    secret++;
                    continue;
                }
                long length;
                try
                {
                    length = new FileInfo(entry).Length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (length >= MaxFileBytes)
                {
                    large++;
                    continue;
                }
                string text;
                try
                {
                    var bytes = File.ReadAllBytes(entry);
                    if (Array.IndexOf(bytes, (byte)0) >= 0)
                    {
                        binary++;
                        continue;
                    }
                    text = strict.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    binary++;
                    continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (text.Length > 0 && text[0] == '﻿') text = text[1..];
                files[Path.GetRelativePath(root, entry).Replace('\\', '/')] = text;
                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }
            }
        }
        return new Manifest(files, binary, large, secret, truncated);
    }

    /// <summary>Split a manifest into chunks whose JSON stays under <paramref name="maxBytes"/> (one chunk when it fits).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Chunk(IReadOnlyDictionary<string, string> files, int maxBytes)
    {
        var chunks = new List<IReadOnlyDictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        long size = 0;
        foreach (var (path, text) in files)
        {
            // JSON escaping can grow text; count it the way it will be sent.
            var cost = (long)Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(path)) + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text)) + 2;
            if (current.Count > 0 && size + cost > maxBytes)
            {
                chunks.Add(current);
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                size = 0;
            }
            current[path] = text;
            size += cost;
        }
        if (current.Count > 0 || chunks.Count == 0) chunks.Add(current);
        return chunks;
    }
}

public static class GuardianSkills
{
    public const int MaxFilesPerSkill = 300;
    public const int MaxBytesPerSkill = 5_000_000;

    /// <summary>A skill folder's text files for a deposit (credential files and binaries are left out), with its
    /// description from SKILL.md's front matter. Null when the folder has no SKILL.md.</summary>
    public static GuardianSkill? Gather(string folder)
    {
        var manifest = Path.Combine(folder, "SKILL.md");
        if (!File.Exists(manifest)) return null;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (GuardianProjects.IsCredentialFile(Path.GetFileName(file)) || relative.Split('/').Any(p => p.StartsWith('.') && p != ".vision")) continue;
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (Array.IndexOf(bytes, (byte)0) >= 0) continue;
                var text = strict.GetString(bytes);
                total += bytes.Length;
                if (total > MaxBytesPerSkill || files.Count >= MaxFilesPerSkill) break;
                files[relative] = text;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // Unreadable or not text: not part of the deposit.
            }
        }
        if (!files.TryGetValue("SKILL.md", out var skillText)) return null;
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return new GuardianSkill(name, Description(skillText) ?? name, files);
    }

    /// <summary>The front matter's description: line.</summary>
    public static string? Description(string skillText)
    {
        var lines = skillText.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return null;
        foreach (var line in lines.Skip(1))
        {
            if (line.Trim() == "---") break;
            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                return line["description:".Length..].Trim().Trim('"', '\'') is { Length: > 0 } d ? d : null;
        }
        return null;
    }

    public enum TakeOutcome { Added, Updated, Unchanged, Skipped }

    /// <summary>Write a skill taken from Guardian into <paramref name="skillsRoot"/>/&lt;slug&gt;. The same files again change
    /// nothing; different files replace the folder only with <paramref name="replace"/> (otherwise it is skipped, so a
    /// local edit is never lost). Paths that would leave the folder are refused.</summary>
    public static TakeOutcome Take(GuardianSkill skill, string skillsRoot, bool replace, out string folder)
    {
        var slug = SkillNaming.Slug(skill.Name);
        if (slug.Length == 0) slug = "guardian-skill";
        folder = Path.Combine(skillsRoot, slug);
        if (!skill.Files.ContainsKey("SKILL.md")) return TakeOutcome.Skipped;
        var full = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var targets = new List<(string Path, string Text)>();
        foreach (var (relative, text) in skill.Files)
        {
            var path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase) || relative.Contains(':')) return TakeOutcome.Skipped;
            targets.Add((path, text));
        }
        if (Directory.Exists(folder))
        {
            var same = targets.All(t => File.Exists(t.Path) && File.ReadAllText(t.Path) == t.Text);
            if (same) return TakeOutcome.Unchanged;
            if (!replace) return TakeOutcome.Skipped;
        }
        var staging = folder + ".guardian-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            foreach (var (path, text) in targets)
            {
                var staged = Path.Combine(staging, Path.GetRelativePath(folder, path));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.WriteAllText(staged, text, new UTF8Encoding(false));
            }
            var existed = Directory.Exists(folder);
            if (existed) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(skillsRoot);
            Directory.Move(staging, folder);
            return existed ? TakeOutcome.Updated : TakeOutcome.Added;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>Where the Settings › Guardian page shows a count: "3 skills".</summary>
public static class GuardianWords
{
    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";
}
