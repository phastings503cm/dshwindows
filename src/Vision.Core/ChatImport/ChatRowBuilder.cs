using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vision.Core;

/// <summary>Builds the stored rows of an imported chat, the same way for every source: user and assistant messages, tool
/// rows (a call's name and one-line arguments, then its result's output and one-line summary), notices, errors and
/// compaction summaries — each at the time the source recorded.</summary>
internal sealed partial class ChatRowBuilder
{
    /// <summary>A tool's output is kept up to this many characters (the rest is noted, not imported) — Vision for macOS's
    /// limit too.</summary>
    public const int MaxToolOutput = 20_000;
    /// <summary>One message is kept up to this many characters.</summary>
    public const int MaxMessage = 1_000_000;
    /// <summary>A tool row's "called with" and "result" lines.</summary>
    public const int MaxArgSummary = 200;
    public const int MaxResultSummary = 200;

    private readonly List<LogItemRow> _rows = [];
    private readonly Dictionary<string, int> _toolsById = new(StringComparer.Ordinal);
    private string? _assistantKey;
    private DateTimeOffset _last;
    private bool _summaryOnly;

    /// <param name="start">The time a row without one of its own gets (the source's start, or the file's).</param>
    /// <param name="summaryOnly">Count and date only: tool outputs aren't kept (a preview's scan).</param>
    public ChatRowBuilder(DateTimeOffset start, bool summaryOnly = false)
    {
        _last = start;
        _summaryOnly = summaryOnly;
    }

    public IReadOnlyList<LogItemRow> Rows => _rows;
    public int Messages => _rows.Count(r => r.Kind is "user" or "assistant");
    public DateTimeOffset? First => _rows.Count > 0 ? _rows[0].At : null;
    public DateTimeOffset? Last => _rows.Count > 0 ? _rows.Max(r => r.At) : null;
    public string? FirstPrompt => _rows.FirstOrDefault(r => r.Kind == "user" && !string.IsNullOrWhiteSpace(r.Text))?.Text;

    /// <summary>The time rows without one of their own get from here on (a session's start, once it is read).</summary>
    public void Clock(DateTimeOffset at)
    {
        if (_rows.Count == 0) _last = at;
    }

    private DateTimeOffset When(DateTimeOffset? at)
    {
        if (at is { } known) _last = known;
        return _last;
    }

    private void Add(string kind, string? text, DateTimeOffset? at, string? toolName = null, string? argSummary = null, string? output = null,
                     bool isError = false, IReadOnlyList<AttachmentBadge>? attachments = null)
    {
        _rows.Add(new LogItemRow("", _rows.Count, kind, text, toolName, argSummary, output, isError, When(at))
        {
            Attachments = attachments is { Count: > 0 } ? attachments : null,
        });
    }

    public void User(string? text, DateTimeOffset? at, IReadOnlyList<AttachmentBadge>? attachments = null)
    {
        var clean = Clean(text);
        _assistantKey = null;
        if (clean.Length == 0 && attachments is not { Count: > 0 }) return;
        Add("user", Cap(clean, MaxMessage), at, attachments: attachments);
    }

    /// <summary>An assistant message. Consecutive parts with the same <paramref name="mergeKey"/> (one reply the source split
    /// into blocks) become one message.</summary>
    public void Assistant(string? text, DateTimeOffset? at, string? mergeKey = null)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return;
        if (mergeKey is not null && mergeKey == _assistantKey && _rows.Count > 0 && _rows[^1].Kind == "assistant")
        {
            var last = _rows[^1];
            _rows[^1] = last with { Text = Cap(last.Text + "\n\n" + clean, MaxMessage) };
            return;
        }
        Add("assistant", Cap(clean, MaxMessage), at);
        _assistantKey = mergeKey;
    }

    public void Notice(string? text, DateTimeOffset? at)
    {
        var clean = Clean(text);
        if (clean.Length > 0) Add("notice", Cap(clean, MaxToolOutput), at);
    }

    public void Error(string? text, DateTimeOffset? at)
    {
        var clean = Clean(text);
        if (clean.Length > 0) Add("error", Cap(clean, MaxToolOutput), at);
    }

    /// <summary>The source compacted its conversation here: everything above is folded into <paramref name="summary"/>, which
    /// is what a continuing model starts from.</summary>
    public void Compaction(string? summary, DateTimeOffset? at)
    {
        var clean = Clean(summary);
        if (clean.Length == 0) return;
        _assistantKey = null;
        var folded = Messages;
        Add("compaction", Cap(clean, MaxMessage), at, argSummary: folded.ToString(CultureInfo.InvariantCulture));
    }

    public void ToolCall(string? id, string name, string? arguments, DateTimeOffset? at)
    {
        _assistantKey = null;
        Add("tool", null, at, string.IsNullOrWhiteSpace(name) ? "tool" : name.Trim(), OneLine(arguments ?? "", MaxArgSummary));
        if (!string.IsNullOrEmpty(id)) _toolsById[id] = _rows.Count - 1;
    }

    /// <summary>A tool's result: onto the call with <paramref name="id"/>, else the last call still waiting for one, else a
    /// row of its own.</summary>
    public void ToolResult(string? id, string? name, string? output, bool isError, DateTimeOffset? at)
    {
        var index = -1;
        if (!string.IsNullOrEmpty(id) && _toolsById.TryGetValue(id, out var byId)) index = byId;
        else
        {
            for (var i = _rows.Count - 1; i >= 0 && index < 0; i--)
                if (_rows[i].Kind == "tool" && _rows[i].Output is null && _rows[i].Text is null) index = i;
        }
        var text = TextUtil.NormalizeNewlines(output ?? "").TrimEnd();
        var summary = Summary(text, isError);
        var kept = _summaryOnly ? null : text.Length == 0 ? null : Cap(text, MaxToolOutput);
        if (index < 0)
        {
            Add("tool", summary, at, string.IsNullOrWhiteSpace(name) ? "tool" : name.Trim(), "", kept, isError);
            return;
        }
        var row = _rows[index];
        _rows[index] = row with { Text = summary, Output = kept, IsError = isError };
        if (!string.IsNullOrEmpty(id)) _toolsById.Remove(id);
    }

    // MARK: - Words

    /// <summary>A result's one line: its first non-empty line (or "failed: …"); "done" for a success with nothing to say.</summary>
    internal static string Summary(string output, bool isError)
    {
        // A line that is only a wrapper's tag (<persisted-output>, <result>) says nothing.
        var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !TagOnly().IsMatch(l));
        if (first is null) return isError ? "failed" : "done";
        var line = OneLine(first, MaxResultSummary);
        return isError ? "failed: " + line : line;
    }

    /// <summary>The arguments a person would recognise a call by — the command, the file, the URL, the query — on one line;
    /// otherwise the arguments as compact JSON.</summary>
    internal static string Arguments(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            var text = input.GetString() ?? "";
            // Some sources send the arguments as JSON text.
            if (text.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    return Arguments(doc.RootElement);
                }
                catch (JsonException)
                {
                    // Not JSON after all.
                }
            }
            return OneLine(text, MaxArgSummary);
        }
        if (input.ValueKind != JsonValueKind.Object) return input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : OneLine(input.GetRawText(), MaxArgSummary);
        foreach (var key in ArgumentKeys)
        {
            if (!input.TryGetProperty(key, out var value)) continue;
            switch (value.ValueKind)
            {
                case JsonValueKind.String when value.GetString() is { Length: > 0 } text:
                    return OneLine(text, MaxArgSummary);
                case JsonValueKind.Array:
                    var parts = value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToList();
                    // ["bash", "-lc", "ls -la"] reads as the command it runs.
                    if (parts.Count == 3 && parts[1] is "-lc" or "-c") return OneLine(parts[2], MaxArgSummary);
                    if (parts.Count > 0) return OneLine(string.Join(" ", parts), MaxArgSummary);
                    break;
            }
        }
        return OneLine(input.GetRawText(), MaxArgSummary);
    }

    private static readonly string[] ArgumentKeys =
    [
        "command", "cmd", "file_path", "filePath", "notebook_path", "pattern", "query", "q", "url", "path", "description", "prompt", "skill",
        "subject", "name", "code", "input",
    ];

    /// <summary>Text on one line, at most <paramref name="max"/> characters (an ellipsis when cut).</summary>
    internal static string OneLine(string text, int max)
    {
        var line = WhiteSpace().Replace(text, " ").Trim();
        return line.Length <= max ? line : TextUtil.Prefix(line, max - 1).TrimEnd() + "…";
    }

    /// <summary>Line endings normalised, trailing space trimmed.</summary>
    internal static string Clean(string? text) => text is null ? "" : TextUtil.NormalizeNewlines(text).Trim('\n', ' ', '\t').TrimEnd();

    internal static string Cap(string text, int max)
    {
        if (text.Length <= max) return text;
        var kept = TextUtil.Prefix(text, max);
        return kept + $"\n… [{(text.Length - kept.Length).ToString(CultureInfo.InvariantCulture)} more characters not imported]";
    }

    /// <summary>An ISO-8601 time, epoch seconds or epoch milliseconds.</summary>
    internal static DateTimeOffset? Time(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return at;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? Epoch(number) : null;
            case JsonValueKind.Number when element.TryGetDouble(out var value):
                return Epoch(value);
            default:
                return null;
        }
    }

    internal static DateTimeOffset? Epoch(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return null;
        try
        {
            return value > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(value * 1000));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A title from a first prompt: its first line, at most 80 characters.</summary>
    internal static string TitleFrom(string? prompt, string fallback)
    {
        var line = (prompt ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? fallback : OneLine(line, 80);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"^</?[A-Za-z][\w-]*>$")]
    private static partial Regex TagOnly();

    /// <summary>Text with the harness's &lt;system-reminder&gt; blocks taken out (Claude Code adds them to prompts and tool
    /// results; they were for that model, not the conversation).</summary>
    internal static string WithoutReminders(string text) =>
        text.Contains("<system-reminder>", StringComparison.Ordinal) ? Reminder().Replace(text, "").Trim() : text;

    [GeneratedRegex(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex Reminder();
}

/// <summary>Reading JSON Lines files a line at a time, whatever their size: a line that isn't JSON is skipped.</summary>
internal static class JsonLines
{
    /// <summary>Each line's JSON root (disposed after <paramref name="visit"/>), with the line's index.</summary>
    public static void Read(string path, Action<JsonElement, int> visit, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        Read(stream, visit, cancellationToken);
    }

    public static void Read(Stream stream, Action<JsonElement, int> visit, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        var index = 0;
        while (reader.ReadLine() is { } line)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var current = index++;
            if (line.Length < 2 || line[0] != '{') continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            using (doc) visit(doc.RootElement, current);
        }
    }
}

/// <summary>Small readers over <see cref="JsonElement"/> that never throw on a missing or mistyped property.</summary>
internal static class JsonRead
{
    public static JsonElement Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string? Str(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static bool Flag(this JsonElement element, string name) => element.Get(name).ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    /// <summary>A message's text: a plain string, or the text of its text blocks (type text / input_text / output_text).</summary>
    public static string Text(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                parts.Add(block.GetString() ?? "");
                continue;
            }
            var type = block.Str("type");
            if (type is null or "text" or "input_text" or "output_text" && block.Str("text") is { } text) parts.Add(text);
        }
        return string.Join("\n\n", parts.Where(p => p.Length > 0));
    }
}
