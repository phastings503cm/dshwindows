using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// Qwen Code parity tools: glob, grep, read_many_files, exit_plan_mode.
// Names match https://qwenlm.github.io/qwen-code-docs/en/developers/tools/

// MARK: - tiny glob matcher

public static class Glob
{
    /// <summary>Convert a glob pattern to an anchored regex. "**/" crosses directories; "*" and "?"
    /// never cross "/". Backslashes in the pattern are treated as "/", and matching ignores case on
    /// Windows, like its file system.</summary>
    public static Regex? ToRegex(string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        var re = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (string.CompareOrdinal(pattern, i, "**/", 0, 3) == 0)
                {
                    re.Append("(?:.*/)?");
                    i += 3;
                    continue;
                }
                if (string.CompareOrdinal(pattern, i, "**", 0, 2) == 0)
                {
                    re.Append(".*");
                    i += 2;
                    continue;
                }
                re.Append("[^/]*");
            }
            else if (c == '?')
            {
                re.Append("[^/]");
            }
            else
            {
                re.Append(Regex.Escape(c.ToString()));
            }
            i++;
        }
        re.Append('$');
        try
        {
            var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
            return new Regex(re.ToString(), options);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static bool Matches(string pattern, string relativePath, bool fileNameOnly = false)
    {
        var regex = ToRegex(pattern);
        if (regex is null) return false;
        var target = relativePath.Replace('\\', '/');
        if (fileNameOnly) target = target[(target.LastIndexOf('/') + 1)..];
        return regex.IsMatch(target);
    }
}

/// <summary>Recursive walks shared by glob and grep.</summary>
public static class RelativeWalk
{
    /// <summary>Dependency and build-output folders that drown real matches (and eat the file
    /// budget). Skipped unless the pattern names them.</summary>
    public static readonly IReadOnlySet<string> Heavy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "__pycache__", "venv",
    };

    /// <summary>Files under <paramref name="root"/> as "/"-separated relative paths. Hidden entries are
    /// skipped; folders in <see cref="Heavy"/> are skipped unless <paramref name="mention"/> contains
    /// their name.</summary>
    public static List<string> Files(string root, int limit = 5000, string? mention = null)
    {
        var output = new List<string>();
        Walk(root, "", output, limit, mention ?? "");
        return output;
    }

    private static void Walk(string dir, string prefix, List<string> output, int limit, string mention)
    {
        if (output.Count >= limit) return;
        foreach (var entry in FileWalk.Entries(dir).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (output.Count >= limit) return;
            var rel = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                if (entry.IsLink) continue;
                if (Heavy.Contains(entry.Name) && !mention.Contains(entry.Name, StringComparison.OrdinalIgnoreCase)) continue;
                Walk(entry.FullPath, rel, output, limit, mention);
            }
            else
            {
                output.Add(rel);
            }
        }
    }
}

// MARK: - glob

public sealed class GlobTool : IToolExecutor
{
    public const string ToolName = "glob";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Find files matching a glob pattern (e.g. 'src/**/*.cs', '*.json'). Returns matching paths relative to the search directory.",
        """{"type":"object","properties":{"pattern":{"type":"string","description":"Glob pattern to match files"},"path":{"type":"string","description":"Directory to search in (default: project folder)"}},"required":["pattern"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var pattern = JsonArgs.String(args, "pattern");
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult<ToolResult>("Error: pattern is required.");
        var (root, _) = context.Policy.Resolve(JsonArgs.String(args, "path") ?? ".");
        if (!Directory.Exists(root)) return Task.FromResult<ToolResult>($"Error: {root} is not a directory.");
        var regex = Glob.ToRegex(pattern);
        if (regex is null) return Task.FromResult<ToolResult>($"Error: invalid glob pattern: {pattern}");

        var matches = new List<string>();
        foreach (var rel in RelativeWalk.Files(root, mention: pattern))
        {
            if (!regex.IsMatch(rel)) continue;
            matches.Add(rel);
            if (matches.Count >= 500) break;
        }
        if (matches.Count == 0) return Task.FromResult<ToolResult>($"No files matched pattern '{pattern}'.");
        matches.Sort(StringComparer.Ordinal);
        var truncated = matches.Count >= 500 ? "\n… (truncated at 500)" : "";
        return Task.FromResult<ToolResult>(string.Join("\n", matches) + truncated +
                                           $"\n({matches.Count} match{(matches.Count == 1 ? "" : "es")})");
    }
}

// MARK: - grep

public sealed class GrepTool : IToolExecutor
{
    public const string ToolName = "grep";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Search file contents with a regex (literal strings work as-is). Searches files under the project folder; pass include to restrict to files matching a glob (e.g. '*.cs'). Returns matching lines as path:line: text.",
        """{"type":"object","properties":{"pattern":{"type":"string","description":"Regex (or literal text) to search for"},"path":{"type":"string","description":"Directory to search in (default: project folder)"},"include":{"type":"string","description":"Only search files matching this glob, e.g. '*.cs'"}},"required":["pattern"]}""");

    private const long MaxFileBytes = 10 * 1024 * 1024;

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var pattern = JsonArgs.String(args, "pattern");
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult<ToolResult>("Error: pattern is required.");
        var include = JsonArgs.String(args, "include");
        var (root, _) = context.Policy.Resolve(JsonArgs.String(args, "path") ?? ".");
        if (!Directory.Exists(root)) return Task.FromResult<ToolResult>($"Error: {root} is not a directory.");

        Regex rx;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline;
        try
        {
            rx = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            // Not a valid regex: search for it literally.
            rx = new Regex(Regex.Escape(pattern), options, TimeSpan.FromSeconds(2));
        }

        var includeIsBare = include is not null && !include.Contains('/') && !include.Contains('\\');
        var includeRegex = include is null ? null : Glob.ToRegex(include);
        var hits = new List<string>();
        var hitFiles = 0;
        var scanned = 0;
        foreach (var rel in RelativeWalk.Files(root, limit: 3000, mention: include))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includeRegex is not null)
            {
                var target = includeIsBare ? rel[(rel.LastIndexOf('/') + 1)..] : rel;
                if (!includeRegex.IsMatch(target)) continue;
            }
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (new FileInfo(full).Length > MaxFileBytes) continue;
            }
            catch (IOException)
            {
                continue;
            }
            if (FileText.Read(full) is not { } read) continue;
            scanned++;
            var text = read.Text.Replace("\r\n", "\n");
            MatchCollection matches;
            try
            {
                matches = rx.Matches(text);
                if (matches.Count == 0) continue;
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            hitFiles++;
            var lineStarts = LineStarts(text);
            var lines = text.Split('\n');
            var lastLine = -1;
            foreach (Match m in matches)
            {
                var lineNo = LineOf(lineStarts, m.Index) + 1;
                if (lineNo == lastLine) continue;
                lastLine = lineNo;
                if (lineNo < 1 || lineNo > lines.Length) continue;
                var snippet = lines[lineNo - 1].Trim();
                if (snippet.Length == 0) continue;
                hits.Add($"{rel}:{lineNo}: {TextUtil.Prefix(snippet, 400)}");
                if (hits.Count >= 200) break;
            }
            if (hits.Count >= 200) break;
        }
        if (hits.Count == 0)
            return Task.FromResult<ToolResult>($"No matches found for '{rx}' ({scanned} files scanned).");
        return Task.FromResult<ToolResult>(string.Join("\n", hits) +
            $"\n({hitFiles} files with matches, {hits.Count} line{(hits.Count == 1 ? "" : "s")})");
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        return starts;
    }

    private static int LineOf(List<int> starts, int index)
    {
        var found = starts.BinarySearch(index);
        return found >= 0 ? found : ~found - 1;
    }
}

// MARK: - read_many_files

public sealed class ReadManyFilesTool : IToolExecutor
{
    public const string ToolName = "read_many_files";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Read several small files at once. `paths` is a comma-separated list of file paths (relative to the project folder). Each file is capped at 500 lines — use read_file for bigger ones.",
        """{"type":"object","properties":{"paths":{"type":"string","description":"Comma-separated list of file paths to read"}},"required":["paths"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var paths = JsonArgs.String(arguments, "paths");
        if (paths is null) return Task.FromResult<ToolResult>("Error: paths (comma-separated file paths) is required.");
        var chunks = new List<string>();
        var failures = new List<string>();
        foreach (var raw in paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (path, _) = context.Policy.Resolve(raw);
            if (FileText.Read(path) is { } read)
            {
                var all = TextUtil.Lines(TextUtil.NormalizeNewlines(read.Text));
                var shown = string.Join("\n", all.Take(500));
                chunks.Add($"=== {Path.GetFileName(path)} ===\n{shown}" +
                           (all.Length > 500 ? $"\n… (truncated, {all.Length} lines total)" : ""));
            }
            else
            {
                failures.Add(raw);
            }
        }
        var output = string.Join("\n\n", chunks);
        if (failures.Count > 0) output += (output.Length == 0 ? "" : "\n\n") + "Could not read: " + string.Join(", ", failures);
        return Task.FromResult<ToolResult>(output.Length == 0 ? "Error: no readable files." : output);
    }
}

// MARK: - exit_plan_mode

/// <summary>Signals the end of planning: the agent asks the user for approval to act.</summary>
public sealed class ExitPlanModeTool : IToolExecutor
{
    public const string ToolName = "exit_plan_mode";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Call this when you have finished writing a plan and want the user to approve starting to implement it. Pass the plan summary in `plan`.",
        """{"type":"object","properties":{"plan":{"type":"string","description":"Short summary of the plan to execute"}},"required":["plan"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var plan = JsonArgs.String(arguments, "plan") ?? "(no plan summary)";
        return Task.FromResult(new ToolResult($"Plan presented to the user: {plan}\nImplementation may begin once approved.") { Plan = plan });
    }
}
