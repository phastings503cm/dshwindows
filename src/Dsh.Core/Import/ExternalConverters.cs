using System.Globalization;

namespace Dsh.Core;

/// <summary>The conversions an import needs beyond what skills, commands and rules already have
/// (<see cref="SkillConverter"/>): subagent definitions, instruction files and saved project notes.</summary>
internal static class ExternalConverters
{
    // MARK: - Subagents

    /// <summary>A subagent definition (~\.claude\agents\name.md, ~\.cursor\agents\name.md) as a skill.
    /// DSH's agent tool starts a generic subagent from a prompt; the skill tells the model to do that
    /// with the definition's instructions, so the specialist still exists — as a recipe, not a
    /// separate kind of object.</summary>
    /// <returns>The skill's folder name and its SKILL.md text; null when the definition has no usable
    /// instructions.</returns>
    public static (string Slug, string Text)? SubagentSkill(ExternalTool tool, string fileName, string sourceText, string? nameOverride = null)
    {
        var source = SkillDocument.Parse(sourceText);
        var body = source.Body.Trim();
        if (body.Length == 0) return null;
        var name = source["name"]?.Trim() is { Length: > 0 } declared ? declared : Path.GetFileNameWithoutExtension(fileName);
        var slug = nameOverride ?? SkillFiles.NonEmpty(SkillNaming.Slug(name), "subagent");
        var description = source["description"]?.Trim() is { Length: > 0 } written ? written : SkillNaming.FirstParagraph(body);
        if (description.Length == 0) description = $"Hand this kind of work to the {name} subagent.";

        var notes = new List<string>();
        var tools = source.List("tools");
        if (tools.Count > 0)
            notes.Add($"In {tool.Label()} it was limited to: {string.Join(", ", tools)}. DSH's subagents get DSH's own tools — ask for the same kind of work.");
        if (source.Bool("readonly") == true) notes.Add("It was read-only in Cursor: tell it not to change any files.");

        var text = new List<string>
        {
            $"# {name} (a {tool.Label()} subagent)",
            "",
            $"This began as a subagent in {tool.Label()}. To use it, hand the work to a subagent: call the `agent` tool with a `prompt` " +
            "that starts with the instructions below and then states the task — the files, the diff, the question. " +
            "A subagent can't ask you anything, so put everything it needs in the prompt.",
        };
        if (notes.Count > 0)
        {
            text.Add("");
            text.AddRange(notes);
        }
        text.Add("");
        text.Add("## Instructions for the subagent");
        text.Add("");
        text.Add(body);

        var doc = new SkillDocument(string.Join("\n", text), hadFrontmatter: true);
        doc["name"] = slug;
        doc["description"] = description;
        return (slug, doc.Render());
    }

    // MARK: - Saved notes

    /// <summary>A note file inside a saved-notes folder.</summary>
    public readonly record struct Note(string Relative, string FullPath, long Bytes);

    internal const int MaxNoteFiles = 500;
    internal const long MaxNoteBytes = 1_000_000;
    internal const long MaxNotesBytes = 10_000_000;

    /// <summary>The .md files under <paramref name="folder"/> (at most four levels down), never through
    /// a link, within sane size limits — notes are text.</summary>
    public static IReadOnlyList<Note> NoteFiles(string folder)
    {
        var output = new List<Note>();
        long total = 0;

        void Visit(string directory, string prefix, int depth)
        {
            if (depth > 4) return;
            foreach (var entry in FileWalk.Entries(directory).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (output.Count >= MaxNoteFiles || total >= MaxNotesBytes) return;
                if (entry.IsLink) continue;
                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                if (entry.IsDirectory)
                {
                    Visit(entry.FullPath, relative, depth + 1);
                    continue;
                }
                if (!entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                long length;
                try
                {
                    length = new FileInfo(entry.FullPath).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (length > MaxNoteBytes) continue;
                total += length;
                output.Add(new Note(relative, entry.FullPath, length));
            }
        }

        if (Directory.Exists(folder)) Visit(folder, "", 0);
        return output;
    }

    /// <summary>Copy <paramref name="notes"/> under <paramref name="destination"/>, keeping their
    /// relative paths and replacing files that are already there.</summary>
    public static void CopyNotes(IEnumerable<Note> notes, string destination)
    {
        foreach (var note in notes)
        {
            var target = Path.Combine([destination, .. note.Relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // A copy that differs may have been edited in DSH: keep it beside the new one.
            if (File.Exists(target) && !SameText(SkillFiles.ReadText(target), SkillFiles.ReadText(note.FullPath))) Backup(target);
            File.Copy(note.FullPath, target, overwrite: true);
        }
    }

    /// <summary>The MEMORY.md a notes folder needs when it came without one: a line per note, from the
    /// note's own name and description when it has them.</summary>
    public static string IndexOf(string folder, IEnumerable<Note> notes)
    {
        var lines = new List<string> { "# Saved notes", "" };
        foreach (var note in notes.Where(n => !n.Relative.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)))
        {
            var doc = SkillFiles.ReadText(Path.Combine([folder, .. note.Relative.Split('/')])) is { } text ? SkillDocument.Parse(text) : null;
            var title = doc?["name"]?.Trim() is { Length: > 0 } named ? named : Path.GetFileNameWithoutExtension(note.Relative);
            var about = doc?["description"]?.Trim() is { Length: > 0 } described ? described : doc is null ? "" : SkillNaming.FirstParagraph(doc.Body, 120);
            lines.Add(about.Length == 0 ? $"- [{title}]({note.Relative})" : $"- [{title}]({note.Relative}) — {about}");
        }
        lines.Add("");
        return string.Join("\n", lines);
    }

    /// <summary>Copy <paramref name="path"/> beside itself as <c>name.yyyyMMdd-HHmmss.bak</c> (numbered if
    /// that exists) — for a file an import is about to replace. Nothing loads a .bak, and each replacement
    /// keeps its own, so an earlier backup is never overwritten.</summary>
    public static string Backup(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{path}.{stamp}.bak";
        for (var n = 2; File.Exists(target); n++) target = $"{path}.{stamp}-{n}.bak";
        File.Copy(path, target);
        return target;
    }

    // MARK: - Comparing

    /// <summary>Whether two texts are the same apart from line endings and trailing whitespace.</summary>
    public static bool SameText(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) == Normalize(b);

    private static string Normalize(string text) =>
        (text.StartsWith('\uFEFF') ? text[1..] : text).Replace("\r\n", "\n").TrimEnd();
}
