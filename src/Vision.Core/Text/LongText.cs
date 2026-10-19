namespace Vision.Core;

// MARK: - Very long messages in the transcript
//
// One message can be a 10,000-line paste or tool output. Laid out in full it is one transcript row hundreds of screens
// tall: WPF measures all of it, a resize re-wraps all of it, and the wheel crawls through it. So a long message shows its
// first lines and "Show more · 10,174 more lines"; Show more lays out the rest inline while that stays cheap, and past
// that opens the whole text in a box of its own height inside the row, virtualized line by line, so no single message
// can freeze the window or break its scrolling. "Show less" goes back.

/// <summary>What a long message's collapsed row shows: its first lines, and how much more there is.</summary>
public sealed record LongTextCut(string Preview, int Lines, int ShownLines, int Characters)
{
    /// <summary>Lines left out of the preview (0 when the cut is by characters inside few long lines).</summary>
    public int HiddenLines => Math.Max(0, Lines - ShownLines);

    /// <summary>Characters left out of the preview.</summary>
    public int HiddenCharacters => Math.Max(0, Characters - Preview.Length);
}

/// <summary>The kinds of text a transcript row holds, each with its own idea of "long".</summary>
public enum LongTextKind
{
    /// <summary>What the person sent: plain text.</summary>
    Prompt,
    /// <summary>An assistant reply or notice: markdown.</summary>
    Reply,
    /// <summary>A tool's output: monospace, in a box of fixed height already.</summary>
    Output,
}

public static class LongText
{
    /// <summary>A prompt longer than this many lines or characters is collapsed (the numbers the large-paste chip's
    /// collapsed rows use too, so a chat looks the same whichever path its long prompt took).</summary>
    public const int PromptLines = 40;
    public const int PromptCharacters = 4_000;

    /// <summary>A reply or notice longer than this is collapsed to its first <see cref="ReplyPreviewLines"/> lines. Replies
    /// run long by nature, so the bar is higher than a prompt's.</summary>
    public const int ReplyLines = 300;
    public const int ReplyCharacters = 24_000;
    public const int ReplyPreviewLines = 80;

    /// <summary>Tool output longer than this shows its first <see cref="OutputPreviewLines"/> lines in its box.</summary>
    public const int OutputLines = 400;
    public const int OutputCharacters = 40_000;
    public const int OutputPreviewLines = 200;

    /// <summary>Expanded text up to this size is laid out inline like any message; beyond it, the expanded text goes in a
    /// virtualized box of its own height, so its cost no longer grows with its length.</summary>
    public const int InlineLines = 800;
    public const int InlineCharacters = 80_000;

    /// <summary>The preview of a long <paramref name="text"/> of <paramref name="kind"/>; null when it is short enough to
    /// show whole. One pass over the text, so a huge message costs one scan.</summary>
    public static LongTextCut? Collapse(string? text, LongTextKind kind) => kind switch
    {
        LongTextKind.Prompt => Collapse(text, PromptLines, PromptCharacters, PromptLines),
        LongTextKind.Reply => Collapse(text, ReplyLines, ReplyCharacters, ReplyPreviewLines),
        _ => Collapse(text, OutputLines, OutputCharacters, OutputPreviewLines),
    };

    /// <summary>The first <paramref name="previewLines"/> lines (at most <paramref name="maxCharacters"/> characters) of a
    /// text with more than <paramref name="maxLines"/> lines or <paramref name="maxCharacters"/> characters; null otherwise.</summary>
    public static LongTextCut? Collapse(string? text, int maxLines, int maxCharacters, int previewLines)
    {
        if (string.IsNullOrEmpty(text)) return null;
        // Cheap first: most messages are short enough that neither limit is near.
        if (text.Length <= maxCharacters && text.Length <= maxLines) return null;
        var lines = 1;
        var cut = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (lines == previewLines) cut = i;
            lines++;
        }
        if (text[^1] == '\n') lines--;
        if (lines <= maxLines && text.Length <= maxCharacters) return null;
        var preview = cut >= 0 ? text[..cut] : text;
        if (preview.Length > maxCharacters) preview = TextUtil.Prefix(preview, maxCharacters);
        preview = preview.TrimEnd('\r');
        var shown = 1;
        foreach (var c in preview)
        {
            if (c == '\n') shown++;
        }
        return new LongTextCut(preview, lines, Math.Min(shown, lines), text.Length);
    }

    /// <summary>"Show more · 10,174 more lines" (or "· 12 KB more" when the cut is inside long lines).</summary>
    public static string MoreLabel(LongTextCut cut) =>
        cut.HiddenLines > 0
            ? $"Show more · {cut.HiddenLines:N0} more {(cut.HiddenLines == 1 ? "line" : "lines")}"
            : $"Show more · {Kilobytes(cut.HiddenCharacters)} more";

    public const string LessLabel = "Show less";

    /// <summary>Whether expanded <paramref name="text"/> is small enough to lay out inline.</summary>
    public static bool FitsInline(string text, int lines) => lines <= InlineLines && text.Length <= InlineCharacters;

    /// <summary>The text's lines for the virtualized box ("\r\n" and "\n" alike; a final newline ends the last line).</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0) return [""];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static string Kilobytes(int characters) =>
        characters < 1024 ? $"{characters:N0} characters" : $"{Math.Max(1, (int)Math.Round(characters / 1024.0)):N0} KB";
}

/// <summary>What Copy puts on the clipboard for a transcript row.</summary>
public static class TranscriptCopy
{
    /// <summary>A prompt as the person sent it: the transcript's own decorations of a /goal or a task ("🎯 /goal …",
    /// "📋 Task: …") come off, so what is copied can be sent again as it was. Anything else is copied exactly.</summary>
    public static string Prompt(string shown)
    {
        foreach (var (prefix, replacement) in Decorations)
        {
            if (shown.StartsWith(prefix, StringComparison.Ordinal)) return replacement + shown[prefix.Length..];
        }
        return shown;
    }

    private static readonly (string Prefix, string Replacement)[] Decorations =
    [
        ("🎯 /goal (resuming) ", "/goal "),
        ("🎯 /goal ", "/goal "),
        ("📋 Task (resuming): ", ""),
        ("📋 Task: ", ""),
    ];
}
