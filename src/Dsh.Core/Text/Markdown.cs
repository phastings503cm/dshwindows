using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Core;

/// <summary>One block of rendered markdown.</summary>
public abstract record MarkdownBlock
{
    public sealed record Paragraph(string Text) : MarkdownBlock;
    public sealed record Heading(int Level, string Text) : MarkdownBlock;
    public sealed record Code(string? Language, string Text) : MarkdownBlock;
    public sealed record ListItem(string Marker, string Text, int Depth) : MarkdownBlock;
    public sealed record Quote(string Text) : MarkdownBlock;
    public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock;
    public sealed record Rule : MarkdownBlock;
}

public static class Markdown
{
    /// <summary>Split text into renderable blocks. Deliberately small: fences, ATX headings,
    /// bullet/ordered lists, block quotes, pipe tables, and thematic breaks. An unterminated fence
    /// still closes at end of input, so a streaming response renders as code while it arrives instead
    /// of showing raw backticks.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string source)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownBlock.Paragraph(string.Join("\n", paragraph)));
            paragraph.Clear();
        }

        var lines = TextUtil.NormalizeNewlines(source).Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i++];
            var trimmed = line.Trim();

            // Fenced code — consume until the closing fence or end of input.
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var body = new List<string>();
                while (i < lines.Length)
                {
                    var next = lines[i++];
                    if (next.Trim().StartsWith(fence, StringComparison.Ordinal)) break;
                    body.Add(next);
                }
                blocks.Add(new MarkdownBlock.Code(language.Length == 0 ? null : language, string.Join("\n", body)));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock.Rule());
                continue;
            }

            if (ParseHeading(trimmed) is { } heading)
            {
                FlushParagraph();
                blocks.Add(heading);
                continue;
            }

            // Pipe table: a header row followed by a |---|---| delimiter.
            if (trimmed.StartsWith('|') && i < lines.Length && IsTableDelimiter(lines[i].Trim()))
            {
                FlushParagraph();
                i++;
                var header = TableCells(trimmed);
                var rows = new List<IReadOnlyList<string>>();
                while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                    rows.Add(TableCells(lines[i++].Trim()));
                blocks.Add(new MarkdownBlock.Table(header, rows));
                continue;
            }

            if (ParseListItem(line) is { } item)
            {
                FlushParagraph();
                blocks.Add(item);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock.Quote(trimmed[1..].Trim()));
                continue;
            }

            paragraph.Add(trimmed);
        }
        FlushParagraph();
        return blocks;
    }

    private static MarkdownBlock? ParseHeading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#' && level < 6) level++;
        if (level == 0) return null;
        var rest = line[level..];
        if (rest.Length > 0 && rest[0] != ' ') return null;
        return new MarkdownBlock.Heading(level, rest.Trim());
    }

    private static MarkdownBlock? ParseListItem(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] is ' ' or '\t') indent++;
        var trimmed = line.Trim();
        var depth = Math.Min(indent / 2, 3);

        foreach (var bullet in new[] { "- ", "* ", "+ " })
        {
            if (!trimmed.StartsWith(bullet, StringComparison.Ordinal)) continue;
            var body = trimmed[2..];
            // Task-list items keep their checkbox as the marker.
            if (body.StartsWith("[ ] ", StringComparison.Ordinal)) return new MarkdownBlock.ListItem("☐", body[4..], depth);
            if (body.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) return new MarkdownBlock.ListItem("☑", body[4..], depth);
            return new MarkdownBlock.ListItem("•", body, depth);
        }
        // Ordered: digits followed by "." or ")".
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits > 0 && trimmed.Length > digits + 1 && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
            return new MarkdownBlock.ListItem(trimmed[..digits] + ".", trimmed[(digits + 2)..], depth);
        return null;
    }

    /// <summary>|---|:--:| and friends — the row that turns the line above it into a table header.</summary>
    private static bool IsTableDelimiter(string line)
    {
        if (!line.StartsWith('|')) return false;
        var cells = TableCells(line);
        return cells.Count > 0 && cells.All(cell =>
        {
            var body = cell.Trim();
            return body.Length > 0 && body.All(c => c is '-' or ':') && body.Contains('-');
        });
    }

    private static IReadOnlyList<string> TableCells(string line)
    {
        var body = line;
        if (body.StartsWith('|')) body = body[1..];
        if (body.EndsWith('|')) body = body[..^1];
        return body.Split('|').Select(c => c.Trim()).ToList();
    }
}

// MARK: - Inline syntax

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strike = 8,
    Link = 16,
}

/// <summary>A run of inline text with one style (and the target when it is a link).</summary>
public sealed record InlineRun(string Text, InlineStyle Style, string? Url = null);

/// <summary>Inline markdown: **bold**, *italic* / _italic_, `code`, ~~strike~~, [links](url),
/// &lt;autolinks&gt; and bare http(s) URLs, with backslash escapes. Malformed markup degrades to plain
/// text rather than vanishing.</summary>
public static partial class MarkdownInline
{
    public static IReadOnlyList<InlineRun> Parse(string text)
    {
        var runs = new List<InlineRun>();
        ParseInto(text, InlineStyle.None, null, runs);
        return Merge(runs);
    }

    private static void ParseInto(string text, InlineStyle style, string? url, List<InlineRun> runs)
    {
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            runs.Add(new InlineRun(buffer.ToString(), style, url));
            buffer.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // Backslash escapes.
            if (c == '\\' && i + 1 < text.Length && char.IsAsciiLetterOrDigit(text[i + 1]) == false && !char.IsWhiteSpace(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // Code spans: a run of N backticks closes on the next run of exactly N.
            if (c == '`')
            {
                var n = RunLength(text, i, '`');
                var close = FindBacktickClose(text, i + n, n);
                if (close >= 0)
                {
                    Flush();
                    var code = text[(i + n)..close];
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                    runs.Add(new InlineRun(code, style | InlineStyle.Code, url));
                    i = close + n;
                    continue;
                }
                buffer.Append(text, i, n);
                i += n;
                continue;
            }

            // [label](target)
            if (c == '[' && url is null && TryLink(text, i, out var label, out var target, out var end))
            {
                Flush();
                ParseInto(label, style | InlineStyle.Link, target, runs);
                i = end;
                continue;
            }

            // <https://…>
            if (c == '<' && url is null)
            {
                var m = AutoLink().Match(text, i);
                if (m.Success && m.Index == i)
                {
                    Flush();
                    runs.Add(new InlineRun(m.Groups[1].Value, style | InlineStyle.Link, m.Groups[1].Value));
                    i += m.Length;
                    continue;
                }
            }

            // Bare URLs.
            if ((c == 'h' || c == 'H') && url is null && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var m = BareUrl().Match(text, i);
                if (m.Success && m.Index == i)
                {
                    var found = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', ')');
                    Flush();
                    runs.Add(new InlineRun(found, style | InlineStyle.Link, found));
                    i += found.Length;
                    continue;
                }
            }

            // ~~strike~~
            if (c == '~' && RunLength(text, i, '~') == 2)
            {
                var close = FindDelimiterClose(text, i + 2, '~', 2);
                if (close > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                {
                    Flush();
                    ParseInto(text[(i + 2)..close], style | InlineStyle.Strike, url, runs);
                    i = close + 2;
                    continue;
                }
            }

            // *emphasis* / **strong** / ***both*** (and the underscore forms at word boundaries).
            if (c is '*' or '_')
            {
                var n = Math.Min(RunLength(text, i, c), 3);
                var opensAfter = i + n;
                var canOpen = opensAfter < text.Length && !char.IsWhiteSpace(text[opensAfter])
                              && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]));
                if (canOpen)
                {
                    var close = FindDelimiterClose(text, opensAfter, c, n);
                    if (close > opensAfter)
                    {
                        var added = n switch
                        {
                            1 => InlineStyle.Italic,
                            2 => InlineStyle.Bold,
                            _ => InlineStyle.Bold | InlineStyle.Italic,
                        };
                        Flush();
                        ParseInto(text[opensAfter..close], style | added, url, runs);
                        i = close + n;
                        continue;
                    }
                }
                buffer.Append(text, i, RunLength(text, i, c));
                i += RunLength(text, i, c);
                continue;
            }

            buffer.Append(c);
            i++;
        }
        Flush();
    }

    private static int RunLength(string text, int start, char c)
    {
        var n = 0;
        while (start + n < text.Length && text[start + n] == c) n++;
        return n;
    }

    private static int FindBacktickClose(string text, int from, int n)
    {
        var i = from;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var run = RunLength(text, i, '`');
                if (run == n) return i;
                i += run;
            }
            else
            {
                i++;
            }
        }
        return -1;
    }

    /// <summary>The start of a closing delimiter run of length ≥ n (preceded by non-space), skipping
    /// code spans and escapes. For single delimiters, a longer run is skipped (it belongs to a nested
    /// strong span).</summary>
    private static int FindDelimiterClose(string text, int from, char delim, int n)
    {
        var i = from;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var run = RunLength(text, i, '`');
                var close = FindBacktickClose(text, i + run, run);
                i = close >= 0 ? close + run : i + run;
                continue;
            }
            if (c == delim)
            {
                var run = RunLength(text, i, delim);
                var precededBySpace = char.IsWhiteSpace(text[i - 1]);
                var followedByWord = delim == '_' && i + run < text.Length && char.IsLetterOrDigit(text[i + run]);
                if (!precededBySpace && !followedByWord && (run == n || (n > 1 && run > n)))
                    return run > n ? i + run - n : i;
                i += run;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static bool TryLink(string text, int start, out string label, out string target, out int end)
    {
        label = target = "";
        end = start;
        var depth = 0;
        var closeBracket = -1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0)
            {
                closeBracket = i;
                break;
            }
        }
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(') return false;
        var parens = 0;
        for (var i = closeBracket + 1; i < text.Length; i++)
        {
            if (text[i] == '(') parens++;
            else if (text[i] == ')' && --parens == 0)
            {
                target = text[(closeBracket + 2)..i].Trim();
                // Drop an optional "title".
                var space = target.IndexOf(' ');
                if (space > 0) target = target[..space];
                target = target.Trim('<', '>');
                label = text[(start + 1)..closeBracket];
                end = i + 1;
                return target.Length > 0;
            }
        }
        return false;
    }

    private static List<InlineRun> Merge(List<InlineRun> runs)
    {
        var merged = new List<InlineRun>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Style == run.Style && merged[^1].Url == run.Url)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            else
                merged.Add(run);
        }
        return merged;
    }

    /// <summary>The text with inline markup removed (for copy, tooltips, and width estimates).</summary>
    public static string Plain(string text) => string.Concat(Parse(text).Select(r => r.Text));

    [GeneratedRegex(@"<(https?://[^>\s]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"https?://[^\s<>\[\]`]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
