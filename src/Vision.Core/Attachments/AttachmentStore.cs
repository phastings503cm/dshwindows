using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - Long attachments, saved with their chat

/// <summary>The full text of a chat's attachments, one folder per chat (<c>%APPDATA%\Vision\attachments\&lt;chat id&gt;\</c>):
/// one .txt per file and an index.json naming them (the same layout and JSON as Vision for macOS). read_attachment reads
/// parts back out of it, split exactly as the message that listed them was (the part size is kept with each file).</summary>
public sealed class AttachmentStore
{
    public sealed record Entry
    {
        /// <summary>The name the model knows the file by (unique in the chat).</summary>
        public string Name { get; init; } = "";
        /// <summary>The text file, relative to the chat's folder.</summary>
        public string File { get; init; } = "";
        public string Summary { get; init; } = "";
        public DigestFormat Format { get; init; }
        public int PartChars { get; init; }
        public int Parts { get; init; }
        public string Sha256 { get; init; } = "";
        public DateTimeOffset SavedAt { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();

    public AttachmentStore(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>The store for one chat under <paramref name="root"/> (the app's attachments folder).</summary>
    public static AttachmentStore ForChat(string chatId, string root)
    {
        var safe = new string(chatId.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        return new AttachmentStore(Path.Combine(root, safe.Length == 0 ? "chat" : safe));
    }

    private string IndexPath => Path.Combine(Directory, "index.json");

    public IReadOnlyList<Entry> Entries()
    {
        lock (_lock) return ReadIndex();
    }

    private List<Entry> ReadIndex()
    {
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(System.IO.File.ReadAllText(IndexPath), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private void WriteIndex(List<Entry> entries)
    {
        var temp = IndexPath + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json), TextUtil.Utf8NoBom);
        System.IO.File.Move(temp, IndexPath, overwrite: true);
    }

    /// <summary>Save a file's full text; returns the name it is saved under ("report.pdf", or "report (2).pdf" when the
    /// chat already holds a different file of that name). The same text saved again keeps its entry.</summary>
    public string Save(string name, AttachmentDigest digest, int partChars)
    {
        var text = digest.Text;
        var bytes = TextUtil.Utf8NoBom.GetBytes(text);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var parts = AttachmentPrep.Split(text, partChars).Count;
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var entries = ReadIndex();
            var index = entries.FindIndex(e => e.Sha256 == hash && IsCopyOf(e.Name, name));
            if (index >= 0)
            {
                entries[index] = entries[index] with { PartChars = partChars, Parts = parts, SavedAt = DateTimeOffset.UtcNow };
                WriteIndex(entries);
                return entries[index].Name;
            }
            var unique = UniqueName(name, entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal));
            var file = $"{entries.Count + 1}-{hash[..12]}.txt";
            var path = Path.Combine(Directory, file);
            System.IO.File.WriteAllBytes(path + ".tmp", bytes);
            System.IO.File.Move(path + ".tmp", path, overwrite: true);
            entries.Add(new Entry
            {
                Name = unique, File = file, Summary = digest.Summary, Format = digest.Format, PartChars = partChars, Parts = parts,
                Sha256 = hash, SavedAt = DateTimeOffset.UtcNow,
            });
            WriteIndex(entries);
            return unique;
        }
    }

    /// <summary>The entry the model means by <paramref name="name"/>: exact, then ignoring case, then by the name without
    /// its extension.</summary>
    public Entry? EntryNamed(string name)
    {
        var all = Entries();
        var wanted = name.Trim();
        if (all.LastOrDefault(e => e.Name == wanted) is { } exact) return exact;
        if (all.LastOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase)) is { } folded) return folded;
        var stem = Path.GetFileNameWithoutExtension(wanted);
        return all.LastOrDefault(e => string.Equals(Path.GetFileNameWithoutExtension(e.Name), stem, StringComparison.OrdinalIgnoreCase));
    }

    public string? TextOf(Entry entry)
    {
        try
        {
            return TextUtil.Utf8NoBom.GetString(System.IO.File.ReadAllBytes(Path.Combine(Directory, entry.File)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // MARK: Attachments of queued prompts

    /// <summary>Where the bytes of queued prompts' attachments are kept, inside the chat's folder (so deleting the chat
    /// deletes them too).</summary>
    public const string QueuedFolder = "queued";

    /// <summary>Keep the attachments of a prompt put on the chat's tasks ("Add to tasks"): each one's bytes under
    /// queued/&lt;batch&gt;/, so the task can send them when its turn comes — after a relaunch too.</summary>
    public IReadOnlyList<QueuedAttachment> SaveQueued(IReadOnlyList<MessageAttachment> attachments)
    {
        if (attachments.Count == 0) return [];
        var batch = Guid.NewGuid().ToString("N")[..12];
        var folder = Path.Combine(Directory, QueuedFolder, batch);
        var output = new List<QueuedAttachment>();
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(folder);
            for (var i = 0; i < attachments.Count; i++)
            {
                var attachment = attachments[i];
                var pasted = attachment.Digest?.Format == DigestFormat.Pasted;
                var bytes = pasted ? TextUtil.Utf8NoBom.GetBytes(attachment.Digest!.Text) : attachment.Data;
                var file = $"{i + 1}-{Convert.ToHexStringLower(SHA256.HashData(bytes))[..12]}.bin";
                var path = Path.Combine(folder, file);
                System.IO.File.WriteAllBytes(path + ".tmp", bytes);
                System.IO.File.Move(path + ".tmp", path, overwrite: true);
                var kind = pasted ? QueuedAttachment.PastedKind
                    : attachment.Kind == AttachmentKind.Image ? QueuedAttachment.ImageKind : QueuedAttachment.FileKind;
                output.Add(new QueuedAttachment(kind, attachment.Name, $"{QueuedFolder}/{batch}/{file}"));
            }
        }
        return output;
    }

    /// <summary>A queued prompt's attachments, ready to send (read again as when they were attached); null when any of them
    /// is gone from the disk, with the first missing name in <paramref name="missing"/>.</summary>
    public IReadOnlyList<MessageAttachment>? LoadQueued(IReadOnlyList<QueuedAttachment> queued, out string? missing)
    {
        missing = null;
        var output = new List<MessageAttachment>();
        foreach (var item in queued)
        {
            byte[] bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(QueuedPath(item));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                missing = item.Name;
                return null;
            }
            output.Add(item.Kind switch
            {
                QueuedAttachment.PastedKind => AttachmentReader.Pasted(TextUtil.Utf8NoBom.GetString(bytes), item.Name),
                QueuedAttachment.ImageKind => new MessageAttachment(AttachmentKind.Image, item.Name, bytes),
                _ => new MessageAttachment(AttachmentKind.File, item.Name, bytes),
            });
        }
        return output;
    }

    /// <summary>Delete what a queued prompt kept (its task was deleted).</summary>
    public void RemoveQueued(IEnumerable<QueuedAttachment> queued)
    {
        lock (_lock)
        {
            foreach (var folder in queued.Select(SafeQueuedFolder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private string? SafeQueuedFolder(QueuedAttachment item)
    {
        try
        {
            var folder = Path.GetDirectoryName(QueuedPath(item));
            // Only a batch folder (queued/<batch>), never the queued folder itself.
            return folder is not null && !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Path.Combine(Directory, QueuedFolder)), StringComparison.OrdinalIgnoreCase)
                ? folder
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The file a queued attachment's bytes are in; only ever inside this chat's queued folder.</summary>
    private string QueuedPath(QueuedAttachment item)
    {
        var root = Path.GetFullPath(Path.Combine(Directory, QueuedFolder));
        var path = Path.GetFullPath(Path.Combine(Directory, item.File.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A queued attachment points outside its chat's folder.");
        return path;
    }

    /// <summary>Remove everything saved for the chat (the chat was deleted).</summary>
    public void RemoveAll()
    {
        lock (_lock)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>"report.pdf" taken → "report (2).pdf", then "report (3).pdf", …</summary>
    internal static string UniqueName(string name, ISet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Whether <paramref name="stored"/> is <paramref name="name"/> or a numbered copy of it.</summary>
    private static bool IsCopyOf(string stored, string name)
    {
        if (stored == name) return true;
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        return stored.StartsWith(stem + " (", StringComparison.Ordinal) && (ext.Length == 0 || stored.EndsWith(ext, StringComparison.Ordinal));
    }
}

// MARK: - read_attachment

/// <summary>Reads the parts of a long attachment back out of the chat's store, or searches it.</summary>
public sealed class ReadAttachmentTool(AttachmentStore store) : IToolExecutor
{
    public const string ToolName = AttachmentPrep.ToolName;
    /// <summary>Matches listed for one search.</summary>
    public const int MaxMatches = 40;

    public string Name => ToolName;

    public ToolSpec Spec { get; } = new(ToolName,
        "Read a file the user attached to this chat that was too long to send whole — the message that carried it lists its parts. Pass the attachment's name and a part number to read that part, or a search to find which parts mention something. With no name, lists the chat's saved attachments.",
        """{"type":"object","properties":{"name":{"type":"string","description":"The attachment's name, as the message gave it (e.g. report.pdf)."},"part":{"type":"integer","description":"Which part to read, from 1."},"search":{"type":"string","description":"Words to look for; lists the matching lines and the parts they are in."}}}""");

    public AttachmentStore Store { get; } = store;

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<ToolResult>(Run(arguments));

    public string Run(string arguments)
    {
        var args = JsonArgs.Object(arguments);
        var name = (JsonArgs.String(args, "name") ?? "").Trim();
        var all = Store.Entries();
        if (name.Length == 0)
        {
            if (all.Count == 0) return "No long attachments are saved with this chat.";
            return string.Join("\n", new[] { "Attachments saved with this chat:" }.Concat(all.Select(e => $"- {e.Name} — {e.Summary} · {e.Parts} parts")));
        }
        if (Store.EntryNamed(name) is not { } entry)
        {
            var saved = all.Select(e => e.Name).ToList();
            return $"Error: no attachment named “{name}” is saved with this chat." +
                   (saved.Count == 0 ? " (None is: every file attached so far was sent whole.)" : $" Saved: {string.Join(", ", saved)}.");
        }
        if (Store.TextOf(entry) is not { } text) return $"Error: the saved text of {entry.Name} is missing.";
        var parts = AttachmentPrep.Split(text, entry.PartChars);
        var unit = entry.Format.Unit();
        if ((JsonArgs.String(args, "search") ?? "").Trim() is { Length: > 0 } search) return Search(search, parts, entry.Name);
        var part = JsonArgs.Int(args, "part", JsonArgs.Int(args, "chunk", 1));
        if (parts.Count == 0 || part < 1 || part > parts.Count) return $"Error: {entry.Name} has {parts.Count} parts (1–{parts.Count}).";
        var chosen = parts[part - 1];
        var where = AttachmentPrep.ContentsLine(part, chosen, unit);
        where = where[(where.IndexOf(' ') + 1)..];
        var next = part < parts.Count ? $" — next: part {part + 1}" : " — that was the last part";
        return $"[{entry.Name} — part {part} of {parts.Count} · {where}]\n{chosen.Text}\n[End of part {part} of {parts.Count}{next}]";
    }

    internal static string Search(string query, IReadOnlyList<AttachmentPart> parts, string name)
    {
        var hits = new List<string>();
        var total = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var lines = parts[index].Text.Split('\n');
            for (var offset = 0; offset < lines.Length; offset++)
            {
                if (!Matches(lines[offset], query)) continue;
                total++;
                if (hits.Count >= MaxMatches) continue;
                var shown = lines[offset].Trim(' ', '\t');
                if (shown.Length > 160) shown = shown[..159] + "…";
                hits.Add($"- part {index + 1}, line {AttachmentFormat.Grouped(parts[index].FirstLine + offset)}: {shown}");
            }
        }
        if (total == 0) return $"“{query}” isn't in {name}.";
        var output = new StringBuilder($"“{query}” in {name}: {AttachmentFormat.Count(total, "match", "matches")}");
        output.Append('\n').Append(string.Join("\n", hits));
        if (total > hits.Count) output.Append($"\n… {total - hits.Count} more; search for something narrower.");
        return output.ToString();
    }

    /// <summary>Case- and accent-insensitive "contains" (as Swift's .caseInsensitive + .diacriticInsensitive).</summary>
    private static bool Matches(string line, string query) =>
        System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(line, query,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
}
