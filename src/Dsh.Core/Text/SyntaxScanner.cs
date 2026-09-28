namespace Dsh.Core;

public enum SyntaxKind : byte { Comment, String, Number, Keyword, Tag }

public readonly record struct SyntaxToken(SyntaxKind Kind, int Start, int Length);

/// <summary>A single-pass, line-at-a-time lexer driven by <see cref="Language"/>: comments and strings
/// win over everything, then bare words are matched against the keyword set. The only state that
/// crosses lines is "inside a block comment", which the editor carries from line to line so a
/// 20 000-line file is coloured incrementally rather than rescanned on every keystroke.</summary>
public static class SyntaxScanner
{
    /// <summary>Scan one line. <paramref name="inBlockComment"/> says whether the line starts inside
    /// a block comment; the return value says whether the next line does.</summary>
    public static bool ScanLine(string line, Language language, bool inBlockComment, List<SyntaxToken> tokens)
    {
        if (language.IsPlain) return false;
        var index = 0;
        var count = line.Length;
        var comparison = language.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var markup = language.Name == "Markup";

        if (inBlockComment)
        {
            if (language.BlockComment is not { } open) return false;
            var close = line.IndexOf(open.Close, StringComparison.Ordinal);
            if (close < 0)
            {
                if (count > 0) tokens.Add(new SyntaxToken(SyntaxKind.Comment, 0, count));
                return true;
            }
            index = close + open.Close.Length;
            tokens.Add(new SyntaxToken(SyntaxKind.Comment, 0, index));
        }

        while (index < count)
        {
            var c = line[index];

            if (language.LineComment is { } lineComment && Matches(line, index, lineComment))
            {
                tokens.Add(new SyntaxToken(SyntaxKind.Comment, index, count - index));
                return false;
            }

            if (language.BlockComment is { } block && Matches(line, index, block.Open))
            {
                var start = index;
                var close = line.IndexOf(block.Close, index + block.Open.Length, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new SyntaxToken(SyntaxKind.Comment, start, count - start));
                    return true;
                }
                index = close + block.Close.Length;
                tokens.Add(new SyntaxToken(SyntaxKind.Comment, start, index - start));
                continue;
            }

            if (markup && c == '<')
            {
                // Tag names: <div, </div, <?xml, <!DOCTYPE.
                var start = index + 1;
                if (start < count && line[start] is '/' or '?' or '!') start++;
                var end = start;
                while (end < count && (char.IsLetterOrDigit(line[end]) || line[end] is '-' or '_' or ':' or '.')) end++;
                if (end > start) tokens.Add(new SyntaxToken(SyntaxKind.Tag, start, end - start));
                index = Math.Max(end, index + 1);
                continue;
            }

            if (language.Quotes.Contains(c))
            {
                var start = index;
                index++;
                while (index < count)
                {
                    if (language.Escapes && line[index] == '\\') { index += 2; continue; }
                    if (line[index] == c) { index++; break; }
                    index++;
                }
                index = Math.Min(index, count);
                tokens.Add(new SyntaxToken(SyntaxKind.String, start, index - start));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = index;
                while (index < count && (char.IsAsciiHexDigit(line[index]) || line[index] is '.' or '_' or 'x' or 'X')) index++;
                tokens.Add(new SyntaxToken(SyntaxKind.Number, start, index - start));
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '@' or '#' or '$' || (c == '-' && language.IgnoreCase && index + 1 < count && char.IsLetter(line[index + 1])))
            {
                var start = index;
                if (c is '@' or '#' or '$' or '-') index++;
                while (index < count && (char.IsLetterOrDigit(line[index]) || line[index] == '_')) index++;
                var length = index - start;
                if (length > 0 && IsKeyword(language, line.AsSpan(start, length), comparison))
                    tokens.Add(new SyntaxToken(SyntaxKind.Keyword, start, length));
                if (index == start) index++;
                continue;
            }

            index++;
        }
        return false;
    }

    /// <summary>Scan a whole text; tokens carry offsets into it. Used by tests and small previews.</summary>
    public static List<SyntaxToken> Scan(string text, Language language)
    {
        var tokens = new List<SyntaxToken>();
        var lineTokens = new List<SyntaxToken>();
        var inComment = false;
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            lineTokens.Clear();
            inComment = ScanLine(line, language, inComment, lineTokens);
            foreach (var token in lineTokens) tokens.Add(token with { Start = token.Start + offset });
            offset += line.Length + 1;
        }
        return tokens;
    }

    private static bool Matches(string line, int index, string needle) =>
        needle.Length > 0 && string.CompareOrdinal(line, index, needle, 0, needle.Length) == 0;

    private static bool IsKeyword(Language language, ReadOnlySpan<char> word, StringComparison comparison)
    {
        if (language.Keywords.Count == 0) return false;
        if (language.Keywords is HashSet<string> set && set.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
            return lookup.Contains(word);
        var text = word.ToString();
        return language.Keywords.Contains(text)
               || (comparison == StringComparison.OrdinalIgnoreCase && language.Keywords.Contains(text.ToLowerInvariant()));
    }
}
