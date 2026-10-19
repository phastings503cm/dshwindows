using System.Text;

namespace Vision.Core;

// MARK: - Long files: a budget, parts, a contents list
//
// A file's text goes to the model whole when it fits a quarter of the route's context window (shared among the
// message's files). A longer one is split into parts at line breaks (a new page / slide / sheet starts a new part once
// the current one is half full); the message carries a contents list and as many leading parts as fit, the full text is
// saved with the chat (AttachmentStore), and the model reads the other parts with the read_attachment tool, which the
// message tells it how to call. The numbers and wording are pinned: Vision for macOS uses the same.

/// <summary>How much of a file's text goes in the message, and how big its parts are. All sizes are UTF-16 code units,
/// at four per token.</summary>
public sealed record AttachmentBudget
{
    public const int CharsPerToken = 4;

    /// <summary>The route's context window, in tokens (at least 4,096).</summary>
    public int Window { get; }
    /// <summary>Tokens of one file's text that go in the message: a quarter of the window, shared among the message's
    /// files, never under 2,000.</summary>
    public int InlineTokens { get; }
    /// <summary>Tokens in one part: a sixteenth of the window, between 1,000 and 8,000 (so a part read back through
    /// read_attachment stays under the engine's tool-result cut), and at most half the inline budget (so the contents list
    /// and the first part fit together).</summary>
    public int PartTokens { get; }

    public AttachmentBudget(int window, int sharing = 1)
    {
        Window = Math.Max(window, 4_096);
        InlineTokens = Math.Max(2_000, Window / 4 / Math.Max(1, sharing));
        PartTokens = Math.Min(Math.Min(8_000, Math.Max(1_000, Window / 16)), InlineTokens / 2);
    }

    public int InlineChars => InlineTokens * CharsPerToken;
    public int PartChars => PartTokens * CharsPerToken;
}

/// <summary>One part of a long text. Lines are 1-based and inclusive; pages are the page / slide / sheet in effect at the
/// part's first and last line, when the text has markers.</summary>
public sealed record AttachmentPart(string Text, int FirstLine, int LastLine, int? FirstPage, int? LastPage);

public static class AttachmentPrep
{
    /// <summary>The tool the model reads parts with.</summary>
    public const string ToolName = "read_attachment";

    /// <summary>"--- Page 3 of 42 ---", "--- Slide 2 of 9 ---", "--- Sheet 1 of 3: Totals ---" → 3, 2, 1.</summary>
    internal static int? SectionNumber(ReadOnlySpan<char> line)
    {
        if (!line.StartsWith("--- ")) return null;
        var rest = line[4..];
        foreach (var word in (ReadOnlySpan<string>)["Page ", "Slide ", "Sheet "])
        {
            if (!rest.StartsWith(word)) continue;
            var after = rest[word.Length..];
            var digits = 0;
            while (digits < after.Length && char.IsAsciiDigit(after[digits])) digits++;
            if (digits == 0 || !after[digits..].StartsWith(" of ")) continue;
            return int.TryParse(after[..digits], out var n) ? n : null;
        }
        return null;
    }

    /// <summary>Split <paramref name="text"/> into parts of at most <paramref name="size"/> UTF-16 units (a single longer
    /// line is cut into pieces of its own).</summary>
    public static IReadOnlyList<AttachmentPart> Split(string text, int size)
    {
        size = Math.Max(size, 64);
        var parts = new List<AttachmentPart>();
        var lines = new List<string>();
        var length = 0;
        var firstLine = 1;
        int? page = null;
        int? firstPage = null;

        void Flush(int lastLine)
        {
            if (lines.Count == 0) return;
            parts.Add(new AttachmentPart(string.Join("\n", lines), firstLine, lastLine, firstPage, page));
            lines.Clear();
            length = 0;
        }

        var all = text.Split('\n');
        var count = text.EndsWith('\n') ? all.Length - 1 : all.Length;
        for (var index = 0; index < count; index++)
        {
            var line = all[index];
            var number = index + 1;
            var section = SectionNumber(line);
            var cost = line.Length + 1;
            if (lines.Count > 0 && ((section is not null && length >= size / 2) || length + cost > size)) Flush(number - 1);
            if (section is not null) page = section;
            if (lines.Count == 0)
            {
                firstLine = number;
                firstPage = page;
            }
            if (cost > size)
            {
                // One very long line: cut at character boundaries (never inside a surrogate pair) into pieces that fit.
                Flush(number - 1);
                var start = 0;
                while (line.Length - start > size - 1)
                {
                    var take = size - 1;
                    if (char.IsHighSurrogate(line[start + take - 1])) take--;
                    parts.Add(new AttachmentPart(line.Substring(start, take), number, number, page, page));
                    start += take;
                }
                if (start < line.Length)
                {
                    lines.Add(line[start..]);
                    length = line.Length - start + 1;
                    firstLine = number;
                    firstPage = page;
                }
                continue;
            }
            lines.Add(line);
            length += cost;
        }
        Flush(count);
        return parts;
    }

    /// <summary>How many parts a digest's text makes under <paramref name="budget"/> (1 = it goes whole).</summary>
    public static int PartCount(AttachmentDigest digest, AttachmentBudget budget)
    {
        if (!digest.Readable || digest.TextLength <= budget.InlineChars || digest.Text.Length == 0) return 1;
        return Split(digest.Text, budget.PartChars).Count;
    }

    /// <summary>"[Attached file: report.pdf — PDF · 42 pages]".</summary>
    public static string Header(string name, AttachmentDigest digest)
    {
        var summary = digest.Summary;
        if (digest.Ocr) summary += " · text read by OCR";
        return $"[Attached file: {name} — {summary}]";
    }

    /// <summary>The contents-list line for one part: <c>3. pages 7–9 · lines 611–902 · "Revenue by region"</c>.</summary>
    public static string ContentsLine(int index, AttachmentPart part, (string One, string Many)? unit)
    {
        var fields = new List<string>();
        if (unit is { } u && part.FirstPage is { } first)
        {
            var last = part.LastPage ?? first;
            fields.Add(first == last ? $"{u.One} {first}" : $"{u.Many} {first}–{last}");
        }
        fields.Add(part.FirstLine == part.LastLine
            ? $"line {AttachmentFormat.Grouped(part.FirstLine)}"
            : $"lines {AttachmentFormat.Grouped(part.FirstLine)}–{AttachmentFormat.Grouped(part.LastLine)}");
        if (OpeningWords(part.Text) is { } opening) fields.Add($"\"{opening}\"");
        return $"{index}. " + string.Join(" · ", fields);
    }

    /// <summary>The first line with words in it (not a page marker), white space collapsed, at most 60 characters.</summary>
    internal static string? OpeningWords(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SectionNumber(line) is not null) continue;
            var words = string.Join(' ', line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim('#', '-', '*', '>', '|', ' ');
            if (words.Length == 0) continue;
            return words.Length > 60 ? words[..59] + "…" : words;
        }
        return null;
    }

    /// <summary>The message text for one file: whole, or the contents list and leading parts (parts &gt; 1).
    /// <paramref name="storedName"/> is the name the full text is saved under; null when it couldn't be saved.</summary>
    public static (string Text, int Parts) InlineText(string name, AttachmentDigest digest, AttachmentBudget budget, string? storedName)
    {
        var head = Header(name, digest);
        if (!digest.Readable) return (head + "\n" + (digest.Problem ?? "Vision found no text in it."), 1);
        var length = digest.Text.Length;
        if (length <= budget.InlineChars) return (head + "\n" + digest.Text + $"\n[End of {name}]", 1);
        var parts = Split(digest.Text, budget.PartChars);
        var total = parts.Count;
        var lines = new List<string> { head };
        var tokens = AttachmentFormat.Grouped(AttachmentFormat.Tokens(length));
        var intro = new StringBuilder($"This file is too long to send whole: about {tokens} tokens, and this model's context window is " +
                                      $"{AttachmentFormat.Grouped(budget.Window)} tokens. Vision split it into {total} parts");
        if (storedName is not null)
        {
            intro.Append($" and saved them with this chat. To read another part, call the {ToolName} tool with " +
                         $"{{\"name\": \"{storedName}\", \"part\": N}}; to find which parts mention something, call it with " +
                         $"{{\"name\": \"{storedName}\", \"search\": \"words to look for\"}}. Read the parts you need before " +
                         "answering about the rest of the file.");
        }
        else
        {
            intro.Append("; only the first parts are below, and the rest could not be kept.");
        }
        lines.Add(intro.ToString());
        // The contents list: every part, or the first hundred and a count when there are more.
        lines.Add($"Contents ({total} parts):");
        var listed = Math.Min(total, 100);
        for (var index = 0; index < listed; index++) lines.Add(ContentsLine(index + 1, parts[index], digest.Format.Unit()));
        if (total > listed) lines.Add($"… and {total - listed} more parts ({listed + 1}–{total}).");
        var text = new StringBuilder(string.Join("\n", lines));
        // Leading parts, as many as the budget holds — always the first.
        var room = budget.InlineChars - text.Length;
        var shown = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var block = $"\n--- Part {index + 1} of {total} ---\n" + parts[index].Text;
            if (shown > 0 && block.Length > room) break;
            text.Append(block);
            room -= block.Length;
            shown++;
        }
        var more = total - shown;
        text.Append(more > 0
            ? $"\n[End of part {shown} of {total} of {name} — {more} more part{(more == 1 ? "" : "s")}: read {(more == 1 ? "it" : "them")} with {ToolName}]"
            : $"\n[End of {name}]");
        return (text.ToString(), total);
    }

    /// <summary>One outgoing message's attachments, prepared, and the badges its transcript row shows.</summary>
    public sealed record Prepared(IReadOnlyList<MessageAttachment> Attachments, IReadOnlyList<AttachmentBadge> Badges);

    /// <summary>Prepare one outgoing message's attachments for a route with this context window: every file read (if it
    /// wasn't yet), each given the text that goes in its place, and every readable file's full text saved to
    /// <paramref name="store"/> (so a long one can be read in parts now, and any of them again after the app restarts).
    /// Reads files, so call it off the UI thread.</summary>
    public static Prepared Prepare(IReadOnlyList<MessageAttachment> attachments, int window, AttachmentStore? store)
    {
        var digested = attachments.Select(a => AttachmentReader.Digested(a)).ToList();
        var textual = digested.Count(a => a.Kind == AttachmentKind.File && a.Digest?.Readable == true);
        var budget = new AttachmentBudget(window, Math.Max(1, textual));
        var output = new List<MessageAttachment>(digested.Count);
        var badges = new List<AttachmentBadge>(digested.Count);
        foreach (var attachment in digested)
        {
            if (attachment.Digest is not { } digest)
            {
                output.Add(attachment);
                badges.Add(AttachmentBadge.Of(attachment));
                continue;
            }
            if (attachment.Kind == AttachmentKind.Image)
            {
                var image = attachment with { Text = AttachmentReader.ImageText(attachment.Name, digest.Text), Parts = 1 };
                output.Add(image);
                badges.Add(AttachmentBadge.Of(image));
                continue;
            }
            string? storedName = null;
            if (digest.Readable && store is not null)
            {
                try
                {
                    storedName = store.Save(attachment.Name, digest, budget.PartChars);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    storedName = null; // it still goes; a long one just can't be read further
                }
            }
            var isLong = digest.Readable && digest.TextLength > budget.InlineChars;
            var inline = InlineText(storedName ?? attachment.Name, digest, budget, isLong ? storedName : null);
            // The full text now lives in the store (or whole in Text); don't carry it twice.
            var prepared = attachment with { Text = inline.Text, Parts = inline.Parts, Digest = digest.WithoutText() };
            output.Add(prepared);
            badges.Add(AttachmentBadge.Of(prepared, storedName));
        }
        return new Prepared(output, badges);
    }
}
