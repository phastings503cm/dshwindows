using System.Security.Cryptography;
using System.Text;

namespace Dsh.Core;

/// <summary>MEMORY.md and the day's memory/YYYY-MM-DD.md are instruction files, so today they are pasted into
/// every prompt in full — fine at twenty lines, a hog at two thousand. This keeps a big one from taking the
/// window over: its opening stays in the prompt, and the whole file is indexed into the memory store, so the
/// parts that matter to a message come with that message instead. Small files are left exactly as they are.</summary>
public static class MemoryInstructions
{
    /// <summary>A memory file up to this many characters (about 400 tokens) stays in the prompt whole.</summary>
    public const int FullLoadChars = 1_500;
    /// <summary>What remains of a bigger one.</summary>
    public const int HeadChars = 1_000;
    /// <summary>The most pieces one file is cut into.</summary>
    public const int MaxChunks = 400;
    /// <summary>Bumped when the way a file is cut into notes changes, so pieces made the old way are made again.</summary>
    private const int ChunkRules = 1;

    /// <summary>Source label of the notes indexed from a file: <c>file:MEMORY.md</c>.</summary>
    public const string SourcePrefix = "file:";

    /// <summary>True for a file named like a memory file (MEMORY.md, memory/YYYY-MM-DD.md).</summary>
    public static bool IsMemoryFile(InstructionFile file) =>
        file.Label.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)
        || file.Label.StartsWith("memory/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The files this applies to: a memory file inside the project folder. (Your own instruction files, kept in
    /// DSH's data folder, and notes imported from elsewhere are not the project's and are left as they are.)</summary>
    private static bool IsProjectMemoryFile(InstructionFile file, string root) =>
        IsMemoryFile(file) && !string.IsNullOrEmpty(root) && InsideFolder(file.Path, root);

    private static bool InsideFolder(string path, string folder)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The context with each big memory file cut to its opening (and indexed in <paramref name="store"/>);
    /// the context itself when there is nothing to cut.</summary>
    public static ProjectContext Slim(ProjectContext context, MemoryStore store)
    {
        Tidy(context.Root, store);
        if (!context.Instructions.Any(f => IsProjectMemoryFile(f, context.Root) && f.Text.Length > FullLoadChars)) return context;
        var slimmed = new List<InstructionFile>();
        foreach (var file in context.Instructions)
        {
            if (!IsProjectMemoryFile(file, context.Root) || file.Text.Length <= FullLoadChars)
            {
                slimmed.Add(file);
                continue;
            }
            var complete = IndexFile(context.Root, file, store);
            slimmed.Add(file with { Text = Head(file.Text, complete) });
        }
        return context with { Instructions = slimmed };
    }

    /// <summary>The opening of a file, cut at a paragraph or line and followed by a note saying where the rest went.</summary>
    internal static string Head(string text, bool complete = true)
    {
        var cut = text[..HeadChars];
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        var paragraph = cut.LastIndexOf("\n\n", StringComparison.Ordinal);
        var line = cut.LastIndexOf('\n');
        var at = paragraph > HeadChars / 2 ? paragraph : line > HeadChars / 2 ? line : cut.Length;
        var omitted = text.Length - at;
        return text[..at].TrimEnd() +
               (complete
                   ? $"\n\n[… {omitted:N0} more characters of this file are searchable: notes from it come with your messages when they're relevant, or the agent can use memory_search.]"
                   : $"\n\n[… {omitted:N0} more characters of this file: the first {MaxChunks} sections are searchable (notes from them come with your messages when they're relevant, or the agent can use memory_search); the rest can only be read from the file.]");
    }

    /// <summary>Forget the notes of a file that has shrunk to a size that needs no indexing, or is gone: what they say
    /// is no longer in the file, and "edit that file" would not change them.</summary>
    private static void Tidy(string root, MemoryStore store)
    {
        if (string.IsNullOrEmpty(root)) return;
        var stale = store.All()
            .Where(n => n.Source.StartsWith(SourcePrefix, StringComparison.Ordinal) && SameFolder(n.Project, root))
            .Select(n => n.Source).Distinct(StringComparer.Ordinal).Where(source => !StillBig(root, source[SourcePrefix.Length..])).ToList();
        foreach (var source in stale)
            store.DeleteWhere(n => n.Source == source && SameFolder(n.Project, root));
    }

    private static bool StillBig(string root, string label)
    {
        try
        {
            var path = Path.Combine(root, label.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            // Characters, not bytes: a Chinese file is three bytes to a character, and one that shrank under the limit in
            // characters is loaded whole again. (Never more characters than bytes, so a small file needs no reading.)
            if (info.Length <= FullLoadChars) return false;
            if (info.Length > 8_000_000) return true;
            return File.ReadAllText(path).Length > FullLoadChars;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // can't tell: leave the notes
        }
    }

    /// <summary>Make the file's notes searchable — once per version of the file, remembered across launches by a
    /// fingerprint on the notes themselves (so nothing is rewritten, and no note gets a new id, at every start).</summary>
    private static bool IndexFile(string root, InstructionFile file, MemoryStore store)
    {
        var source = SourcePrefix + file.Label;
        // (The fingerprint covers how the file is cut up and which secrets are screened out as well as its text, so a change to
        // either set of rules rebuilds old pieces.)
        var version = "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ChunkRules + "\u0001" + SecretGuard.Revision + "\u0001" + file.Text)), 0, 6).ToLowerInvariant();
        bool Mine(MemoryItem n) => n.Source == source && SameFolder(n.Project, root);
        // Indexed already: the pieces say whether they are all of it.
        if (store.Any(n => Mine(n) && n.Tags.Contains(version))) return store.Any(n => Mine(n) && n.Tags.Contains(version) && n.Tags.Contains(WholeTag));
        var all = MemoryFiles.Chunk(file.Text, source, file.Label, root, MemoryKinds.Note, ["file", version]);
        var kept = all.Take(MaxChunks).ToList();
        // Whole: nothing past the limit, and no piece left out for looking like a key (either would make "searchable" untrue).
        var complete = all.Count <= MaxChunks && !MemoryStore.FlagSecrets(kept).Any(f => f);
        var chunks = kept.Select(d => d with { Tags = [.. d.Tags, complete ? WholeTag : "part"] }).ToList();
        // The file is the source of truth: notes from an older version of it are replaced, not piled up.
        store.DeleteWhere(Mine);
        store.Import(chunks);
        return complete;
    }

    /// <summary>On every piece of a file when the pieces are all of it.</summary>
    private const string WholeTag = "whole";

    private static bool SameFolder(string? a, string b) =>
        a is not null && string.Equals(MemoryStore.NormalizeProject(a), MemoryStore.NormalizeProject(b), StringComparison.OrdinalIgnoreCase);
}
