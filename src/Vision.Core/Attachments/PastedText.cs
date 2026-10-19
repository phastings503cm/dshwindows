namespace Vision.Core;

// MARK: - Very large pastes become an attachment
//
// Pasting a 10,000-line log into the composer used to put all of it in the text box — and then, sent, all of it in one
// transcript row, a read-only TextBox that WPF lays out in full. A paste over either threshold becomes a
// "Pasted text · 10,214 lines · 412 KB" chip instead (view, edit, remove), sent through the same path as an attached
// file, so a long one goes in parts the model reads on demand. Pinned: Vision for macOS uses the same numbers.

public static class PastedText
{
    /// <summary>A paste with more lines than this becomes an attachment.</summary>
    public const int MaxLines = 400;
    /// <summary>A paste with more characters (UTF-16 units) than this becomes an attachment.</summary>
    public const int MaxCharacters = 20_000;

    /// <summary>Whether <paramref name="text"/> is too large to paste into the composer as it is.</summary>
    public static bool IsLarge(string text)
    {
        // Cheap first: the character count settles most pastes without counting lines.
        if (text.Length > MaxCharacters) return true;
        return AttachmentFormat.LineCount(AttachmentFormat.NormalizeNewlines(text)) > MaxLines;
    }

    /// <summary>The attachment for a large paste, named so several in one chat stay apart ("pasted-text.txt",
    /// "pasted-text-2.txt", …) given the names already staged or sent.</summary>
    public static MessageAttachment Attachment(string text, IEnumerable<string>? existing = null) =>
        AttachmentReader.Pasted(text, Name(existing ?? []));

    public static string Name(IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains("pasted-text.txt")) return "pasted-text.txt";
        var n = 2;
        while (taken.Contains($"pasted-text-{n}.txt")) n++;
        return $"pasted-text-{n}.txt";
    }

    /// <summary>A user message's text shown collapsed in the transcript when it is longer than this many lines or
    /// characters (older chats hold pastes from before the chip).</summary>
    public const int CollapseLines = 40;
    public const int CollapseCharacters = 4_000;

    /// <summary>The first lines of a long message, for its collapsed transcript row; null when it is short enough to show.
    /// Counts lines only once, so a huge message costs one pass.</summary>
    public static (string Preview, int Lines)? Collapsed(string text)
    {
        var lines = 0;
        var cut = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines++;
            if (lines == CollapseLines) cut = i;
        }
        var total = text.Length == 0 ? 0 : text[^1] == '\n' ? lines : lines + 1;
        if (total <= CollapseLines && text.Length <= CollapseCharacters) return null;
        var preview = cut >= 0 ? text[..cut] : text;
        if (preview.Length > CollapseCharacters)
        {
            var take = CollapseCharacters;
            if (char.IsHighSurrogate(preview[take - 1])) take--;
            preview = preview[..take];
        }
        return (preview, total);
    }
}
