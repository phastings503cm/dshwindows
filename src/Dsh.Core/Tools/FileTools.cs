namespace Dsh.Core;

// MARK: - read_file

public sealed class ReadFileTool : IToolExecutor
{
    public const string ToolName = "read_file";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Read a text file. Returns contents with 1-based line numbers. Use start_line/end_line for large files (defaults to the first 1000 lines). Paths may be relative to the project folder.",
        """{"type":"object","properties":{"file_path":{"type":"string","description":"Path of the file to read"},"start_line":{"type":"integer","description":"First line to read (1-based, inclusive)"},"end_line":{"type":"integer","description":"Last line to read (inclusive)"},"description":{"type":"string","description":"Short reason for reading"}},"required":["file_path"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var raw = JsonArgs.String(args, "file_path");
        if (string.IsNullOrEmpty(raw)) return Task.FromResult<ToolResult>("Error: file_path is required.");
        var (path, _) = context.Policy.Resolve(raw);
        var name = Path.GetFileName(path);
        if (Directory.Exists(path))
            return Task.FromResult<ToolResult>($"Error: {name} is a directory. Use list_directory to see what is in it.");
        var read = FileText.Read(path);
        if (read is null)
            return Task.FromResult<ToolResult>($"Error: cannot read {name}: not a readable text file (binary or missing).");

        // CRLF and lone CR become LF so line math is sane.
        var lines = TextUtil.Lines(TextUtil.NormalizeNewlines(read.Value.Text));
        var total = lines.Length;
        var start = Math.Max(1, JsonArgs.Int(args, "start_line", 1));
        var end = Math.Min(total, Math.Max(start, JsonArgs.Int(args, "end_line", Math.Min(total, start + 999))));
        if (start > end || start > total)
            return Task.FromResult<ToolResult>($"Error: no such line range (file has {total} lines).");
        var output = string.Join("\n", Enumerable.Range(start, end - start + 1).Select(n => $"{n} | {lines[n - 1]}"));
        var more = end < total ? $"\n\n(File has {total} lines total; showing {start}-{end}.)" : "";
        return Task.FromResult<ToolResult>(output + more);
    }
}

// MARK: - write_file

public sealed class WriteFileTool : IToolExecutor
{
    public const string ToolName = "write_file";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Create or overwrite a file with the given contents. Parent directories are created as needed.",
        """{"type":"object","properties":{"file_path":{"type":"string"},"content":{"type":"string","description":"Full file contents"},"description":{"type":"string"}},"required":["file_path","content"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var raw = JsonArgs.String(args, "file_path");
        var content = JsonArgs.String(args, "content");
        if (raw is null || content is null)
            return Task.FromResult<ToolResult>("Error: file_path and content are required.");
        var (path, _) = context.Policy.Resolve(raw);
        var existed = File.Exists(path);
        try
        {
            // Rewriting an existing file keeps its BOM and line endings.
            var format = existed && FileText.Read(path) is { } previous ? previous.Format : TextFileFormat.Default;
            if (content.Contains("\r\n", StringComparison.Ordinal)) format = format with { UsesCrlf = false };
            FileText.Write(path, content, format);
            var change = new FileChange(path, existed ? FileChangeKind.Modified : FileChangeKind.Created);
            return Task.FromResult(new ToolResult($"Wrote {content.Length} characters to {Path.GetFileName(path)}.") { Files = [change] });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult<ToolResult>($"Error: write failed: {ex.Message}");
        }
    }
}

// MARK: - edit (old_string/new_string)

public sealed class EditTool : IToolExecutor
{
    public const string ToolName = "edit";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Replace an exact block of text in a file. old_string must match exactly (including whitespace) and must be unique in the file unless replace_all is true. Creates the file with just new_string when the file does not exist.",
        """{"type":"object","properties":{"file_path":{"type":"string"},"old_string":{"type":"string","description":"Exact text to find"},"new_string":{"type":"string","description":"Replacement text"},"replace_all":{"type":"boolean","description":"Replace every occurrence"},"description":{"type":"string"}},"required":["file_path","old_string","new_string"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var raw = JsonArgs.String(args, "file_path");
        var oldText = JsonArgs.String(args, "old_string");
        var newText = JsonArgs.String(args, "new_string");
        if (raw is null || oldText is null || newText is null)
            return Task.FromResult<ToolResult>("Error: file_path, old_string and new_string are required.");
        var replaceAll = JsonArgs.Bool(args, "replace_all", false);
        var (path, _) = context.Policy.Resolve(raw);
        var name = Path.GetFileName(path);

        try
        {
            if (!File.Exists(path))
            {
                // New file from an edit: only the replacement text lands in it.
                FileText.Write(path, newText, TextFileFormat.Default);
                return Task.FromResult(new ToolResult($"Created {name} with the new block.")
                {
                    Files = [new FileChange(path, FileChangeKind.Created)],
                });
            }
            if (FileText.Read(path) is not { } read)
                return Task.FromResult<ToolResult>($"Error: cannot edit {name}: it is not a UTF-8 text file.");

            // The model sees files with LF line endings (read_file normalizes them), so a CRLF file
            // is matched and edited in LF form and written back as CRLF.
            var crlf = read.Format.UsesCrlf;
            var existing = crlf ? read.Text.Replace("\r\n", "\n") : read.Text;
            var needle = crlf ? oldText.Replace("\r\n", "\n") : oldText;
            var replacement = crlf ? newText.Replace("\r\n", "\n") : newText;

            var occurrences = TextUtil.CountOccurrences(existing, needle);
            if (occurrences < 1)
                return Task.FromResult<ToolResult>($"Error: old_string not found in {name}. Read the file and copy the exact text (whitespace matters).");
            if (occurrences > 1 && !replaceAll)
                return Task.FromResult<ToolResult>($"Error: old_string matches {occurrences} times. Include more surrounding lines to make it unique, or set replace_all=true.");

            string updated;
            if (replaceAll)
            {
                updated = existing.Replace(needle, replacement, StringComparison.Ordinal);
            }
            else
            {
                var at = existing.IndexOf(needle, StringComparison.Ordinal);
                updated = string.Concat(existing.AsSpan(0, at), replacement, existing.AsSpan(at + needle.Length));
            }
            FileText.Write(path, updated, read.Format);
            var n = replaceAll ? occurrences : 1;
            return Task.FromResult(new ToolResult($"Edited {name}: replaced {n} occurrence{(n == 1 ? "" : "s")}.")
            {
                Files = [new FileChange(path, FileChangeKind.Modified)],
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult<ToolResult>($"Error: {ex.Message}");
        }
    }
}

// MARK: - list_directory

public sealed class ListDirectoryTool : IToolExecutor
{
    public const string ToolName = "list_directory";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "List files and directories. Non-recursive by default; pass recursive=true for a full tree.",
        """{"type":"object","properties":{"path":{"type":"string","description":"Directory to list (default: project folder)"},"recursive":{"type":"boolean"}},"required":[]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var raw = JsonArgs.String(args, "path") ?? ".";
        var (path, _) = context.Policy.Resolve(raw);
        var recursive = JsonArgs.Bool(args, "recursive", false);
        if (!Directory.Exists(path)) return Task.FromResult<ToolResult>($"Error: {path} is not a directory.");
        try
        {
            var lines = recursive ? Walk(path) : ListOne(path);
            return Task.FromResult<ToolResult>(lines.Count == 0 ? "(empty directory)" : string.Join("\n", lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<ToolResult>($"Error: {ex.Message}");
        }
    }

    private static List<string> ListOne(string path) =>
        FileWalk.Entries(path)
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => (e.IsDirectory ? "📁 " : "  ") + e.Name)
            .ToList();

    private static List<string> Walk(string root)
    {
        var output = new List<string>();
        void Visit(string dir, int depth)
        {
            foreach (var entry in FileWalk.Entries(dir).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (output.Count >= 500) return;
                output.Add(new string(' ', depth * 2) + (entry.IsDirectory ? "📁 " : "  ") + entry.Name);
                if (output.Count >= 500)
                {
                    output.Add("… (truncated at 500 entries)");
                    return;
                }
                if (entry.IsDirectory) Visit(entry.FullPath, depth + 1);
            }
        }
        Visit(root, 0);
        return output;
    }
}

/// <summary>Directory reading shared by the file tools: hidden entries (dot-names, and anything
/// Windows marks hidden or system) are skipped, and symlinked directories are never descended.</summary>
public static class FileWalk
{
    public readonly record struct Entry(string Name, string FullPath, bool IsDirectory, bool IsLink);

    public static IEnumerable<Entry> Entries(string directory)
    {
        IEnumerable<FileSystemInfo> infos;
        try
        {
            infos = new DirectoryInfo(directory).EnumerateFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }
        using var e = infos.GetEnumerator();
        while (true)
        {
            FileSystemInfo info;
            try
            {
                if (!e.MoveNext()) yield break;
                info = e.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }
            if (IsHidden(info)) continue;
            var isDir = (info.Attributes & FileAttributes.Directory) != 0;
            yield return new Entry(info.Name, info.FullName, isDir, IsLink(info));
        }
    }

    /// <summary>A symlink or junction — something that points elsewhere and is never followed.
    /// Other reparse points (OneDrive's Files On-Demand, dedup, AppExecLink aliases) are ordinary
    /// files and folders; treating them as links would hide a whole OneDrive-synced project.</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable reparse data: err on the side of not following it.
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
    }

    public static bool IsHidden(FileSystemInfo info)
    {
        if (info.Name.StartsWith('.')) return true;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
