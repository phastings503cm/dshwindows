using System.Globalization;
using System.Text;

namespace Vision.Core;

// MARK: - Text out of Rich Text (.rtf)
//
// Vision for macOS hands RTF to textutil; Windows has no such tool, so this reads the text runs itself: control words
// that are text (\par, \line, \tab, \'hh, \uN, \emdash …) become characters, destinations that aren't text (font and
// colour tables, stylesheets, pictures, document info, anything marked \*) are skipped, and the rest is dropped.

public static class RtfText
{
    /// <summary>Destinations whose content is never text.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "headerl", "headerr", "headerf",
        "footerl", "footerr", "footerf", "themedata", "colorschememapping", "datastore", "latentstyles", "listtable",
        "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "mmathPr", "filetbl", "revtbl", "pgdsctbl", "fldinst",
        "bkmkstart", "bkmkend", "nonshppict",
    };

    /// <summary>The text of an RTF document; null when it isn't one.</summary>
    public static string? Extract(byte[] data)
    {
        // RTF is 7-bit ASCII; anything else came from \'hh escapes in the document's code page (1252 assumed).
        var source = Encoding.Latin1.GetString(data);
        if (!source.StartsWith("{\\rtf", StringComparison.Ordinal)) return null;
        var output = new StringBuilder(source.Length / 2);
        // Per group: whether it is skipped, and how many characters follow a \uN (its \ucN).
        var stack = new Stack<(bool Skip, int UnicodeSkip)>();
        var skip = false;
        var unicodeSkip = 1;
        var pendingSkip = 0; // characters still to drop after a \uN
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            switch (c)
            {
                case '{':
                    stack.Push((skip, unicodeSkip));
                    i++;
                    // "{\*\destination …}": a destination this reader doesn't know — skip it.
                    if (i + 1 < source.Length && source[i] == '\\' && source[i + 1] == '*') skip = true;
                    continue;
                case '}':
                    if (stack.Count > 0) (skip, unicodeSkip) = stack.Pop();
                    pendingSkip = 0;
                    i++;
                    continue;
                case '\\':
                {
                    i++;
                    if (i >= source.Length) break;
                    var next = source[i];
                    if (next is '\\' or '{' or '}')
                    {
                        Emit(next);
                        i++;
                        continue;
                    }
                    if (next == '\'')
                    {
                        if (i + 2 < source.Length && byte.TryParse(source.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                        {
                            Emit(Cp1252(b));
                            i += 3;
                        }
                        else
                        {
                            i++;
                        }
                        continue;
                    }
                    if (next == '*')
                    {
                        skip = true;
                        i++;
                        continue;
                    }
                    if (next is '~') { Emit(' '); i++; continue; }
                    if (next is '-') { i++; continue; } // optional hyphen
                    if (next is '_') { Emit('‑'); i++; continue; }
                    if (next is '\n' or '\r') { Emit('\n'); i++; continue; } // "\" + line break = \par
                    if (!char.IsAsciiLetter(next))
                    {
                        i++;
                        continue;
                    }
                    var wordStart = i;
                    while (i < source.Length && char.IsAsciiLetter(source[i])) i++;
                    var word = source[wordStart..i];
                    int? parameter = null;
                    var paramStart = i;
                    if (i < source.Length && (source[i] == '-' || char.IsAsciiDigit(source[i])))
                    {
                        i++;
                        while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
                        if (int.TryParse(source.AsSpan(paramStart, i - paramStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) parameter = n;
                    }
                    if (i < source.Length && source[i] == ' ') i++; // the delimiter space belongs to the control word
                    Control(word, parameter);
                    continue;
                }
                case '\r' or '\n':
                    i++;
                    continue;
                default:
                    Emit(c);
                    i++;
                    continue;
            }
        }
        var lines = AttachmentFormat.NormalizeNewlines(output.ToString()).Split('\n').Select(l => l.TrimEnd());
        return TextScan.Tidy(lines);

        void Emit(char ch)
        {
            if (skip) return;
            if (pendingSkip > 0)
            {
                pendingSkip--;
                return;
            }
            output.Append(ch);
        }

        void Control(string word, int? parameter)
        {
            if (Skipped.Contains(word))
            {
                skip = true;
                return;
            }
            switch (word)
            {
                case "par" or "line" or "sect" or "page" or "row": Emit('\n'); break;
                case "tab": Emit('\t'); break;
                case "cell": Emit('\t'); break;
                case "emdash": Emit('—'); break;
                case "endash": Emit('–'); break;
                case "bullet": Emit('•'); break;
                case "lquote": Emit('‘'); break;
                case "rquote": Emit('’'); break;
                case "ldblquote": Emit('“'); break;
                case "rdblquote": Emit('”'); break;
                case "uc": unicodeSkip = Math.Max(0, parameter ?? 1); break;
                case "u" when parameter is { } code:
                    var value = code < 0 ? code + 65536 : code;
                    if (!skip) output.Append((char)value);
                    pendingSkip = unicodeSkip;
                    break;
            }
        }
    }

    private static char Cp1252(byte b) => b is >= 0x80 and <= 0x9F
        ? "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ"[b - 0x80]
        : (char)b;
}
