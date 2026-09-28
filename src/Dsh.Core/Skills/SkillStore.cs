using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Errors

public enum SkillErrorKind { Exists, NotFound, Invalid, TooLarge, Unsupported, Io }

/// <summary>A skill operation that failed. <see cref="Exception.Message"/> is written for the user;
/// <see cref="Kind"/> lets a caller react (an <see cref="SkillErrorKind.Exists"/> conflict offers
/// Replace / Keep both).</summary>
public sealed class SkillException : Exception
{
    public SkillErrorKind Kind { get; }

    private SkillException(SkillErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public static SkillException Exists(string message) => new(SkillErrorKind.Exists, message);
    public static SkillException NotFound(string message) => new(SkillErrorKind.NotFound, message);
    public static SkillException Invalid(string message) => new(SkillErrorKind.Invalid, message);
    public static SkillException TooLarge(string message) => new(SkillErrorKind.TooLarge, message);
    public static SkillException Unsupported(string message) => new(SkillErrorKind.Unsupported, message);
    public static SkillException Io(string message, Exception? inner = null) => new(SkillErrorKind.Io, message, inner);
}

/// <summary>What to do when the destination already holds a skill of that name.</summary>
public enum ConflictPolicy { Fail, Replace, Rename }

// MARK: - Safe file copying

internal static class SkillFiles
{
    public const int MaxFileBytes = 5_000_000;
    public const int MaxTotalBytes = 25_000_000;
    public const int MaxFiles = 2_000;

    /// <summary>VCS/dependency folders and OS clutter (macOS .DS_Store, Windows Thumbs.db /
    /// desktop.ini) that never belong in a skill.</summary>
    public static readonly IReadOnlySet<string> SkipNames = new HashSet<string>(StringComparer.Ordinal)
    {
        ".DS_Store", ".git", ".svn", "node_modules", "__pycache__", ".venv", "venv", "Thumbs.db", "desktop.ini",
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Copy a folder's regular files. Links (symlinks, junctions, any reparse point) are never
    /// followed or copied — a skill must not smuggle in a link to ~/.ssh — VCS/dependency folders are
    /// skipped, and size/count limits keep an import from copying a whole repository.
    /// <paramref name="excluding"/> names top-level entries to leave out. Returns the file count.</summary>
    public static int CopyTree(string source, string destination, IReadOnlySet<string>? excluding = null)
    {
        var root = new DirectoryInfo(source);
        if (!root.Exists) throw SkillException.Io($"Can't read {LastComponent(source)}.");
        Directory.CreateDirectory(destination);
        var files = 0;
        long total = 0;

        void Visit(DirectoryInfo dir, string target, string rel)
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // The folder itself must be readable; an unreadable subfolder is skipped.
                if (rel.Length == 0) throw SkillException.Io($"Can't read {LastComponent(source)}.", ex);
                return;
            }
            foreach (var entry in entries)
            {
                var name = entry.Name;
                if (SkipNames.Contains(name) || (rel.Length == 0 && excluding?.Contains(name) == true)) continue;
                if (IsLink(entry)) continue;
                var entryRel = rel.Length == 0 ? name : rel + "/" + name;
                var dest = Path.Combine(target, name);
                if (entry is DirectoryInfo subdirectory)
                {
                    Directory.CreateDirectory(dest);
                    Visit(subdirectory, dest, entryRel);
                    continue;
                }
                if (entry is not FileInfo file) continue;
                var size = file.Length;
                if (size > MaxFileBytes)
                    throw SkillException.TooLarge($"{entryRel} is {size / 1_000_000} MB; skill files may be at most {MaxFileBytes / 1_000_000} MB.");
                total += size;
                files++;
                if (files > MaxFiles || total > MaxTotalBytes)
                    throw SkillException.TooLarge($"That folder is too large to be a skill (over {MaxFiles} files or {MaxTotalBytes / 1_000_000} MB).");
                File.Copy(file.FullName, dest);
            }
        }

        Visit(root, destination, "");
        return files;
    }

    /// <summary>Put <paramref name="staged"/> at <paramref name="final"/>, honouring the conflict
    /// policy. Returns where it landed.</summary>
    public static string Place(string staged, string final, ConflictPolicy conflict)
    {
        var dest = final;
        if (Path.Exists(dest))
        {
            switch (conflict)
            {
                case ConflictPolicy.Fail:
                    throw SkillException.Exists($"A skill named “{LastComponent(final)}” already exists there.");
                case ConflictPolicy.Replace:
                    Delete(dest);
                    break;
                default:
                    var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(final)) ?? "";
                    var name = SkillNaming.Unique(LastComponent(final), n => Path.Exists(Path.Combine(parent, n)));
                    dest = Path.Combine(parent, name);
                    break;
            }
        }
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dest)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        MoveDirectory(staged, dest);
        return dest;
    }

    /// <summary>A fresh (not yet created) folder under the temp directory.</summary>
    public static string StagingDirectory() => Path.Combine(Path.GetTempPath(), $"dsh-skill-{Guid.NewGuid():N}");

    /// <summary>Move a folder. The staging folder lives on the system drive and a project may not, so a
    /// move that fails across volumes becomes copy + delete.</summary>
    public static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   && Directory.Exists(source) && !Path.Exists(destination))
        {
            // Fall through to copying.
        }
        try
        {
            CopyAll(new DirectoryInfo(source), destination);
        }
        catch
        {
            DeleteQuietly(destination);
            throw;
        }
        DeleteQuietly(source);
    }

    private static void CopyAll(DirectoryInfo source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (IsLink(entry)) continue;
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo dir) CopyAll(dir, target);
            else File.Copy(entry.FullName, target);
        }
    }

    /// <summary>Whether <paramref name="info"/> is a symlink or junction. Unknown counts as a link:
    /// links are never followed. Other reparse points are ordinary files — OneDrive's Files
    /// On-Demand marks every synced file as one.</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return FileWalk.IsLink(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether the file or folder at <paramref name="path"/> is itself a link.</summary>
    public static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && IsLink(info);
    }

    /// <summary>Delete a file or folder permanently. A link is removed, never what it points to.</summary>
    public static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    public static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }

    /// <summary>The last path component, ignoring a trailing separator (Swift's lastPathComponent).</summary>
    public static string LastComponent(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Whether two paths name the same location (case-insensitive on Windows).</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary><paramref name="s"/>, or <paramref name="fallback"/> when it is empty.</summary>
    public static string NonEmpty(string s, string fallback) => s.Length == 0 ? fallback : s;

    /// <summary>UTF-8 (no BOM), written atomically.</summary>
    public static void WriteText(string path, string text) => FileText.Write(path, text, TextFileFormat.Default);

    public static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

// MARK: - Conversion between layouts

public static class SkillConverter
{
    /// <summary>Write <paramref name="skill"/> as a portable skill folder (SKILL.md + bundled files) at
    /// <paramref name="directory"/>. A Cursor rule keeps its globs/alwaysApply; a Claude command
    /// becomes a skill only the user can invoke.</summary>
    public static void WriteSkillFolder(Skill skill, string directory, string? name = null)
    {
        if (skill.Kind == SkillKind.Skill)
        {
            SkillFiles.CopyTree(skill.Directory, directory);
            if (name is not null && skill.Document() is { } renamed) // renamed on import
            {
                renamed["name"] = name;
                SkillFiles.WriteText(Path.Combine(directory, "SKILL.md"), renamed.Render());
            }
            return;
        }
        if (skill.Document() is not { } source) throw SkillException.Io($"Can't read {Path.GetFileName(skill.Path)}.");
        var doc = new SkillDocument(source.Body, hadFrontmatter: true);
        doc["name"] = name ?? SkillFiles.NonEmpty(SkillNaming.Slug(skill.Name), "skill");
        doc["description"] = skill.Description;
        if (skill.Globs.Count > 0) doc.SetList("globs", skill.Globs);
        if (skill.AlwaysApply) doc["alwaysApply"] = "true";
        if (skill.ArgumentHint is { } hint) doc["argument-hint"] = hint;
        if (skill.AllowedTools.Count > 0) doc.SetList("allowed-tools", skill.AllowedTools);
        if (skill.Kind == SkillKind.Command || !skill.ModelInvocable) doc["disable-model-invocation"] = "true";
        Directory.CreateDirectory(directory);
        SkillFiles.WriteText(Path.Combine(directory, "SKILL.md"), doc.Render());
    }

    /// <summary>Cursor .mdc text for a skill.</summary>
    public static string? CursorRule(Skill skill)
    {
        if (skill.Document() is not { } source) return null;
        var doc = new SkillDocument(source.Body, hadFrontmatter: true);
        doc["description"] = skill.Description;
        if (skill.Globs.Count > 0) doc.SetList("globs", skill.Globs);
        doc["alwaysApply"] = skill.AlwaysApply ? "true" : "false";
        var extras = skill.Resources();
        if (extras.Count > 0)
        {
            doc.Body += $"\n\n<!-- Bundled files (copied next to this rule in .cursor/skills/{skill.Slug}/): {string.Join(", ", extras.Take(12))} -->";
        }
        return doc.Render();
    }

    /// <summary>Claude .claude/commands/&lt;name&gt;.md text for a skill.</summary>
    public static string? ClaudeCommand(Skill skill)
    {
        if (skill.Document() is not { } source) return null;
        var doc = new SkillDocument(source.Body, hadFrontmatter: true);
        doc["description"] = skill.Description;
        if (skill.ArgumentHint is { } hint) doc["argument-hint"] = hint;
        if (skill.AllowedTools.Count > 0) doc.SetList("allowed-tools", skill.AllowedTools);
        return doc.Render();
    }
}

// MARK: - Drafts (awaiting approval)

/// <summary>A skill that exists but isn't active yet: AI-generated, proposed by the agent, or imported
/// "for review". Drafts live in the app's data folder, not in any repository; approving one moves it
/// to its destination.</summary>
public sealed record SkillDraft
{
    /// <summary>The draft's folder (holds SKILL.md, draft.json and any bundled files).</summary>
    public required string Directory { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>Where it goes when approved.</summary>
    public SkillScope Scope { get; init; } = SkillScope.User;
    public string? ProjectPath { get; init; }
    /// <summary>"ai" (generated in the app), "agent" (proposed mid-chat), "chat" (from a
    /// conversation), "import".</summary>
    public required string Source { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Note { get; init; }

    /// <summary>Stable identity: the folder name.</summary>
    public string Id => SkillFiles.LastComponent(Directory);
    public string SkillFile => Path.Combine(Directory, "SKILL.md");

    public string SourceLabel => Source switch
    {
        "agent" => "Proposed by the agent",
        "ai" => "Generated with AI",
        "chat" => "Made from a chat",
        "import" => "Imported",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Source.ToLowerInvariant()),
    };
}

/// <summary>draft.json: where an approved draft goes and where it came from.</summary>
internal sealed record DraftMeta
{
    [JsonConverter(typeof(SkillScopeJsonConverter))]
    public required SkillScope Scope { get; init; }
    public string? ProjectPath { get; init; }
    public required string Source { get; init; }
    [JsonConverter(typeof(Iso8601JsonConverter))]
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Note { get; init; }
}

/// <summary>"project" / "user".</summary>
internal sealed class SkillScopeJsonConverter : JsonConverter<SkillScope>
{
    public override SkillScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "project" => SkillScope.Project,
                "user" => SkillScope.User,
                var other => throw new JsonException($"Unknown skill scope “{other}”."),
            }
            : throw new JsonException("A skill scope must be a string.");

    public override void Write(Utf8JsonWriter writer, SkillScope value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.RawValue());
}

/// <summary>ISO-8601 in UTC to the second ("2026-09-28T21:26:00Z"), the format the Mac app writes.</summary>
internal sealed class Iso8601JsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            return value;
        throw new JsonException("Expected an ISO-8601 date.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DraftMeta))]
internal sealed partial class DraftMetaJsonContext : JsonSerializerContext;

public static class SkillDrafts
{
    internal const string MetaName = "draft.json";

    /// <summary>Every draft, newest first.</summary>
    public static IReadOnlyList<SkillDraft> List(SkillLocations? locations = null)
    {
        locations ??= SkillLocations.Standard;
        var output = new List<SkillDraft>();
        foreach (var entry in FileWalk.Entries(locations.Drafts))
        {
            if (!entry.IsDirectory) continue;
            if (SkillFiles.ReadText(Path.Combine(entry.FullPath, "SKILL.md")) is not { } text) continue;
            var doc = SkillDocument.Parse(text);
            var meta = ReadMeta(entry.FullPath);
            output.Add(new SkillDraft
            {
                Directory = entry.FullPath,
                Name = doc["name"] ?? entry.Name,
                Description = doc["description"] ?? SkillNaming.FirstParagraph(doc.Body),
                Scope = meta.Scope,
                ProjectPath = meta.ProjectPath,
                Source = meta.Source,
                CreatedAt = meta.CreatedAt,
                Note = meta.Note,
            });
        }
        return output.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    private static DraftMeta ReadMeta(string directory)
    {
        var fallback = new DraftMeta { Scope = SkillScope.User, Source = "import", CreatedAt = DateTimeOffset.MinValue };
        if (SkillFiles.ReadText(Path.Combine(directory, MetaName)) is not { } json) return fallback;
        try
        {
            return JsonSerializer.Deserialize(json, DraftMetaJsonContext.Default.DraftMeta) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static void WriteMeta(DraftMeta meta, string directory) =>
        SkillFiles.WriteText(Path.Combine(directory, MetaName), JsonSerializer.Serialize(meta, DraftMetaJsonContext.Default.DraftMeta));

    /// <summary>Turn free text into a well-formed SKILL.md: a valid slug name, a description, and the
    /// body. Text without frontmatter is accepted (the name/description then come from the
    /// fallbacks).</summary>
    public static (string Slug, string Text) Normalize(string text, string? fallbackName = null,
                                                       string? fallbackDescription = null)
    {
        var doc = SkillDocument.Parse(text);
        var rawName = (doc["name"] ?? fallbackName ?? "").Trim();
        var slug = SkillNaming.Slug(rawName);
        if (slug.Length == 0) throw SkillException.Invalid("The skill needs a name (letters, numbers, hyphens).");
        var description = (doc["description"] ?? fallbackDescription ?? SkillNaming.FirstParagraph(doc.Body)).Trim();
        if (description.Length == 0) throw SkillException.Invalid("The skill needs a description saying when to use it.");
        if (doc.Body.Trim().Length == 0) throw SkillException.Invalid("The skill has no instructions.");
        // Name first, description second, whatever else the author had after.
        var ordered = new SkillDocument(doc.Body, hadFrontmatter: true);
        ordered["name"] = slug;
        ordered["description"] = description;
        foreach (var key in doc.Order)
        {
            if (key is "name" or "description") continue;
            if (doc.Fields.TryGetValue(key, out var value)) ordered[key] = value;
            else if (doc.Lists.TryGetValue(key, out var list)) ordered.SetList(key, list);
        }
        return (slug, ordered.Render());
    }

    /// <summary>Save a new draft from full SKILL.md text.</summary>
    public static SkillDraft Create(string text, SkillScope scope, string? projectRoot, string source,
                                    string? note = null, string? fallbackName = null,
                                    string? fallbackDescription = null, SkillLocations? locations = null)
    {
        locations ??= SkillLocations.Standard;
        var (slug, normalized) = Normalize(text, fallbackName, fallbackDescription);
        Directory.CreateDirectory(locations.Drafts);
        var dirName = SkillNaming.Unique(slug, n => Path.Exists(Path.Combine(locations.Drafts, n)));
        var dir = Path.Combine(locations.Drafts, dirName);
        Directory.CreateDirectory(dir);
        SkillFiles.WriteText(Path.Combine(dir, "SKILL.md"), normalized);
        WriteMeta(NewMeta(scope, projectRoot, source, note), dir);
        return Find(dir, locations);
    }

    /// <summary>Stage a whole skill folder (with bundled files) as a draft.</summary>
    public static SkillDraft Stage(string folder, SkillScope scope, string? projectRoot, string source,
                                   string? note = null, SkillLocations? locations = null)
    {
        locations ??= SkillLocations.Standard;
        Directory.CreateDirectory(locations.Drafts);
        var slug = SkillFiles.NonEmpty(SkillNaming.Slug(SkillFiles.LastComponent(folder)), "skill");
        var dirName = SkillNaming.Unique(slug, n => Path.Exists(Path.Combine(locations.Drafts, n)));
        var dir = Path.Combine(locations.Drafts, dirName);
        try
        {
            SkillFiles.CopyTree(folder, dir);
        }
        catch
        {
            SkillFiles.DeleteQuietly(dir); // no half-copied draft
            throw;
        }
        if (!File.Exists(Path.Combine(dir, "SKILL.md")))
        {
            SkillFiles.DeleteQuietly(dir);
            throw SkillException.Invalid($"{SkillFiles.LastComponent(folder)} has no SKILL.md.");
        }
        WriteMeta(NewMeta(scope, projectRoot, source, note), dir);
        return Find(dir, locations);
    }

    private static DraftMeta NewMeta(SkillScope scope, string? projectRoot, string source, string? note) =>
        new() { Scope = scope, ProjectPath = projectRoot, Source = source, CreatedAt = DateTimeOffset.UtcNow, Note = note };

    private static SkillDraft Find(string dir, SkillLocations locations) =>
        List(locations).FirstOrDefault(d => SkillFiles.SamePath(d.Directory, dir))
        ?? throw SkillException.Io("Couldn't save the draft.");

    /// <summary>Replace a draft's SKILL.md (the review editor's Save).</summary>
    public static void Update(SkillDraft draft, string text)
    {
        var (_, normalized) = Normalize(text);
        SkillFiles.WriteText(draft.SkillFile, normalized);
    }

    /// <summary>Change where an approved draft will go.</summary>
    public static void Retarget(SkillDraft draft, SkillScope scope, string? projectRoot)
    {
        WriteMeta(new DraftMeta
        {
            Scope = scope,
            ProjectPath = projectRoot ?? draft.ProjectPath,
            Source = draft.Source,
            CreatedAt = draft.CreatedAt,
            Note = draft.Note,
        }, draft.Directory);
    }

    /// <summary>Where approving would put it.</summary>
    public static string Destination(SkillDraft draft, string? projectRoot, SkillLocations? locations = null)
    {
        locations ??= SkillLocations.Standard;
        var slug = SkillFiles.NonEmpty(SkillNaming.Slug(draft.Name), draft.Id);
        if (draft.Scope == SkillScope.User) return Path.Combine(locations.UserSkills, slug);
        var root = projectRoot ?? draft.ProjectPath
                   ?? throw SkillException.Invalid("Open a project first, or make this a user skill (available in every project).");
        return Path.Combine(SkillLocations.ProjectSkills(root), slug);
    }

    /// <summary>Activate a draft: move it to its destination and delete the draft. Returns the path of
    /// the activated SKILL.md.</summary>
    public static string Approve(SkillDraft draft, string? projectRoot, ConflictPolicy conflict = ConflictPolicy.Fail,
                                 SkillLocations? locations = null)
    {
        var dest = Destination(draft, projectRoot, locations);
        var staged = SkillFiles.StagingDirectory();
        try
        {
            SkillFiles.CopyTree(draft.Directory, staged, excluding: new HashSet<string>(StringComparer.Ordinal) { MetaName });
            var landed = SkillFiles.Place(staged, dest, conflict);
            SkillFiles.DeleteQuietly(draft.Directory);
            return Path.Combine(landed, "SKILL.md");
        }
        catch
        {
            SkillFiles.DeleteQuietly(staged);
            throw;
        }
    }

    public static void Reject(SkillDraft draft) => Directory.Delete(draft.Directory, recursive: true);
}

// MARK: - Managing active skills

public static class SkillManager
{
    /// <summary>Moves a file or folder to the Recycle Bin and says whether it could. The Windows app sets
    /// this; when it is null, <see cref="Trash"/> deletes permanently.</summary>
    public static Func<string, bool>? RecycleBin { get; set; }

    /// <summary>A starting point for a hand-written skill.</summary>
    public static string Scaffold(string name, string description) =>
        $"""
        ---
        name: {SkillFiles.NonEmpty(SkillNaming.Slug(name), "new-skill")}
        description: {(description.Length == 0 ? "Use when …" : description)}
        ---

        # {name}

        ## When to use

        ## Steps

        1.
        """;

    /// <summary>The folder a new skill of this scope is written under.</summary>
    public static string Base(SkillScope scope, string? projectRoot, SkillLocations? locations = null)
    {
        locations ??= SkillLocations.Standard;
        if (scope == SkillScope.User) return locations.UserSkills;
        if (projectRoot is null) throw SkillException.Invalid("Open a project first, or make this a user skill.");
        return SkillLocations.ProjectSkills(projectRoot);
    }

    /// <summary>Write a new active skill from SKILL.md text. Returns the path of its SKILL.md.</summary>
    public static string Create(string text, SkillScope scope, string? projectRoot,
                                ConflictPolicy conflict = ConflictPolicy.Fail, SkillLocations? locations = null)
    {
        var (slug, normalized) = SkillDrafts.Normalize(text);
        var baseDir = Base(scope, projectRoot, locations);
        var staged = SkillFiles.StagingDirectory();
        Directory.CreateDirectory(staged);
        try
        {
            SkillFiles.WriteText(Path.Combine(staged, "SKILL.md"), normalized);
            var landed = SkillFiles.Place(staged, Path.Combine(baseDir, slug), conflict);
            return Path.Combine(landed, "SKILL.md");
        }
        catch
        {
            SkillFiles.DeleteQuietly(staged);
            throw;
        }
    }

    /// <summary>Copy a Claude/Cursor/Agents skill, rule or command into DSH's own store so it can be
    /// edited and shared without touching the other tool's files. Returns the new SKILL.md.</summary>
    public static string Adopt(Skill skill, SkillScope scope, string? projectRoot,
                               ConflictPolicy conflict = ConflictPolicy.Rename, SkillLocations? locations = null)
    {
        var baseDir = Base(scope, projectRoot, locations);
        var slug = SkillFiles.NonEmpty(SkillNaming.Slug(skill.Name), "skill");
        var staged = SkillFiles.StagingDirectory();
        try
        {
            SkillConverter.WriteSkillFolder(skill, staged, slug);
            var landed = SkillFiles.Place(staged, Path.Combine(baseDir, slug), conflict);
            return Path.Combine(landed, "SKILL.md");
        }
        catch
        {
            SkillFiles.DeleteQuietly(staged);
            throw;
        }
    }

    /// <summary>Full text of the file ("" when it can't be read).</summary>
    public static string Read(Skill skill) => SkillFiles.ReadText(skill.Path) ?? "";

    /// <summary>Save edits (only for skills this app owns — foreign files are adopted first).</summary>
    public static void Write(Skill skill, string text)
    {
        if (!skill.IsOwned)
            throw SkillException.Unsupported($"This skill belongs to {skill.Origin.Label()}. Copy it to DSH first to edit it.");
        SkillFiles.WriteText(skill.Path, text);
    }

    /// <summary>Remove a skill (its folder, or the rule/command file) — to the Recycle Bin when
    /// <see cref="RecycleBin"/> is set, so it stays recoverable; works for foreign files too.</summary>
    public static void Trash(Skill skill)
    {
        var target = skill.Kind == SkillKind.Skill ? skill.Directory : skill.Path;
        if (!Path.Exists(target)) throw SkillException.NotFound($"{SkillFiles.LastComponent(target)} doesn't exist.");
        if (RecycleBin is { } recycle)
        {
            if (!recycle(target)) throw SkillException.Io($"Couldn't move {SkillFiles.LastComponent(target)} to the Recycle Bin.");
            return;
        }
        SkillFiles.Delete(target);
    }
}

// MARK: - Linting

public enum SkillIssueSeverity { Info = 0, Warning = 1, Error = 2 }

public sealed record SkillIssue(SkillIssueSeverity Severity, string Message)
{
    public string Id => $"{(int)Severity}:{Message}";
}

public static partial class SkillLint
{
    /// <summary>Problems with a SKILL.md, most severe first.</summary>
    public static IReadOnlyList<SkillIssue> Check(string text)
    {
        var output = new List<SkillIssue>();
        void Add(SkillIssueSeverity severity, string message) => output.Add(new SkillIssue(severity, message));

        var doc = SkillDocument.Parse(text);
        if (!doc.HadFrontmatter)
            Add(SkillIssueSeverity.Error, "Add a frontmatter block (--- name: … description: … ---) at the top.");
        var name = doc["name"]?.Trim() ?? "";
        if (name.Length == 0)
        {
            Add(SkillIssueSeverity.Error, "Missing `name`.");
        }
        else if (!SkillNaming.IsValidName(name))
        {
            Add(SkillIssueSeverity.Warning,
                $"Use a lowercase-hyphen name ({SkillFiles.NonEmpty(SkillNaming.Slug(name), "my-skill")}) — Claude and Cursor expect it, max 64 characters.");
        }
        var description = doc["description"]?.Trim() ?? "";
        if (description.Length == 0)
        {
            Add(SkillIssueSeverity.Error, "Missing `description`: it is the only thing the model sees before deciding to load the skill.");
        }
        else
        {
            if (description.Length > 1024) Add(SkillIssueSeverity.Warning, "The description is over 1,024 characters; other tools truncate it.");
            if (description.Length < 25) Add(SkillIssueSeverity.Warning, "The description is very short — say when to use the skill and what it covers.");
            var lower = description.ToLowerInvariant();
            if (!(lower.Contains("use ", StringComparison.Ordinal) || lower.Contains("when", StringComparison.Ordinal)
                  || lower.Contains("if ", StringComparison.Ordinal)))
                Add(SkillIssueSeverity.Info, "Say WHEN it applies (e.g. “Use when debugging …”), not only what it is.");
        }
        var body = doc.Body.Trim();
        if (body.Length == 0) Add(SkillIssueSeverity.Error, "The skill has no instructions.");
        var lines = body.Split('\n').Length;
        if (lines > 500)
            Add(SkillIssueSeverity.Warning, $"{lines} lines is long — move reference material into files next to SKILL.md and say when to read them.");
        if (SecretPattern().IsMatch(text))
            Add(SkillIssueSeverity.Warning, "This looks like it contains a secret (key/token). Skills are shared as plain files — remove it.");
        return output.OrderByDescending(i => i.Severity).ToList();
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SecretPattern();
}
