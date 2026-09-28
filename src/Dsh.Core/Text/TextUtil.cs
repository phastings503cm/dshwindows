using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Core;

/// <summary>String helpers with Swift-like prefix/suffix semantics that never split a surrogate pair
/// (a lone surrogate in a request body makes some servers reject it).</summary>
public static partial class TextUtil
{
    /// <summary>At most <paramref name="max"/> characters from the start.</summary>
    public static string Prefix(string s, int max)
    {
        if (max <= 0) return "";
        if (s.Length <= max) return s;
        var end = max;
        if (char.IsHighSurrogate(s[end - 1])) end--;
        return s[..end];
    }

    /// <summary>At most <paramref name="max"/> characters from the end.</summary>
    public static string Suffix(string s, int max)
    {
        if (max <= 0) return "";
        if (s.Length <= max) return s;
        var start = s.Length - max;
        if (char.IsLowSurrogate(s[start])) start++;
        return s[start..];
    }

    /// <summary>Split on "\n", keeping empty entries (Swift's components(separatedBy: "\n")).</summary>
    public static string[] Lines(string s) => s.Split('\n');

    /// <summary>Normalize CRLF and lone CR to LF.</summary>
    public static string NormalizeNewlines(string s) =>
        s.Contains('\r') ? s.Replace("\r\n", "\n").Replace('\r', '\n') : s;

    /// <summary>The first line of <paramref name="s"/> (the whole string if it has none).</summary>
    public static string FirstLine(string s)
    {
        foreach (var line in s.Split('\n'))
        {
            if (line.Length > 0) return line;
        }
        return s;
    }

    /// <summary>Count non-overlapping occurrences of <paramref name="needle"/> (ordinal).</summary>
    public static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>Remove ANSI escape sequences (CSI, OSC, and two-byte escapes) that some tools emit
    /// even when their output is piped.</summary>
    public static string StripAnsi(string s) => s.Contains('\u001b') ? AnsiRegex().Replace(s, "") : s;

    /// <summary>Make captured console output readable as plain text: strip ANSI, normalize line
    /// endings, and apply carriage-return overwrites (progress bars) the way a terminal would.</summary>
    public static string CleanConsoleOutput(string s)
    {
        s = StripAnsi(s).Replace("\r\n", "\n");
        if (!s.Contains('\r')) return s;
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var cr = line.LastIndexOf('\r');
            if (cr < 0) continue;
            // Text after the last CR overwrote the start of the line; keep the tail of the longer
            // earlier text only when the overwrite was shorter (e.g. "100%\r 50%").
            var tail = line[(cr + 1)..];
            var before = line[..cr];
            var prevCr = before.LastIndexOf('\r');
            var previous = prevCr >= 0 ? before[(prevCr + 1)..] : before;
            lines[i] = tail.Length >= previous.Length ? tail : tail + previous[tail.Length..];
        }
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])")]
    private static partial Regex AnsiRegex();

    /// <summary>UTF-8 without BOM, the encoding every file write in the harness uses.</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Strict UTF-8: decoding invalid bytes throws, which is how "not a text file" is detected.</summary>
    public static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
