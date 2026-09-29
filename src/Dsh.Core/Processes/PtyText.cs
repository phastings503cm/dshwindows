using System.Globalization;
using System.Text;

namespace Dsh.Core;

// MARK: - Output hygiene

/// <summary>Terminal noise removal for text headed to the model: strip CSI/OSC/DCS sequences and C0
/// controls except newline/tab, keep only the last fragment of carriage-return-overwritten lines
/// (spinners, progress bars), and collapse long blank runs. A regex-free scanner, since this runs on
/// every read of a chatty process.
///
/// On Windows the input is a pseudo console's output, which is not the program's bytes but a VT
/// rendering of the console screen: runs of blanks can arrive as cursor-forward moves, and repaints
/// as absolute cursor positioning. So beyond stripping, a few cursor moves are translated into the
/// text they stand for — cursor-forward becomes spaces, a move to another row becomes a line break,
/// and a move back to the start of the same row acts like a carriage return.</summary>
public static class PtyText
{
    private const char Esc = '\u001B';
    /// <summary>A bogus cursor-forward count must not turn into megabytes of spaces.</summary>
    private const int MaxForward = 400;

    public static string Clean(string raw)
    {
        if (raw.Length == 0) return "";
        var scanner = new Scanner(raw);
        var text = scanner.Run();
        return CollapseLines(text);
    }

    // MARK: - Lines

    /// <summary>Keep the last fragment of CR-overwritten lines, trim what the console padded lines
    /// with, and let at most two blank lines through in a row.</summary>
    private static string CollapseLines(string text)
    {
        var lines = text.Split('\n');
        var output = new StringBuilder(text.Length);
        var blanks = 0;
        var first = true;
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.Contains('\r'))
            {
                // Keep only the last non-empty fragment (a spinner's final state); "line\r" from a
                // CRLF ending keeps "line".
                var fragments = line.Split('\r', StringSplitOptions.RemoveEmptyEntries);
                line = fragments.Length == 0 ? "" : fragments[^1];
            }
            line = line.TrimEnd(' ');
            if (line.Trim(' ', '\t').Length == 0)
            {
                blanks++;
                if (blanks > 2) continue;
            }
            else
            {
                blanks = 0;
            }
            if (!first) output.Append('\n');
            output.Append(line);
            first = false;
        }
        return output.ToString();
    }

    // MARK: - Escape scanner

    private sealed class Scanner(string raw)
    {
        private readonly StringBuilder _out = new(raw.Length);
        private int _index;
        /// <summary>The screen row the text is being written on, when a cursor-position sequence has
        /// told us (1-based). Lets a repaint of the same row act like a carriage return instead of
        /// starting a new line.</summary>
        private int? _row;

        private bool LineHasText => _out.Length > 0 && _out[^1] != '\n';

        public string Run()
        {
            while (_index < raw.Length)
            {
                var c = raw[_index++];
                switch (c)
                {
                    case Esc:
                        Escape();
                        break;
                    case '\n':
                        _out.Append('\n');
                        if (_row is { } row) _row = row + 1;
                        break;
                    case '\r':
                        // Kept raw: a line full of CRs is a spinner, collapsed afterwards.
                        _out.Append('\r');
                        break;
                    case '\t':
                        _out.Append('\t');
                        break;
                    case < ' ' or '\u007F':
                        // Bell, backspace and the other C0 controls (and DEL) carry nothing readable.
                        break;
                    case >= '\u0080' and <= '\u009F':
                        // C1 controls: the 8-bit CSI form is rare enough to drop like any other.
                        break;
                    default:
                        _out.Append(c);
                        break;
                }
            }
            return _out.ToString();
        }

        private void Escape()
        {
            if (_index >= raw.Length) return;
            var first = raw[_index++];
            switch (first)
            {
                case '[':
                    Csi();
                    break;
                case ']':
                    // OSC (window title, hyperlinks, shell integration): up to BEL or ST.
                    SkipString(bellEnds: true);
                    break;
                case 'P' or 'X' or '^' or '_':
                    // DCS, SOS, PM, APC: up to ST.
                    SkipString(bellEnds: false);
                    break;
                case Esc:
                    // A doubled ESC: treat the second as the start of the next sequence.
                    _index--;
                    break;
                case >= ' ' and <= '/':
                    // Intermediate bytes (charset designation "ESC ( B", "ESC # 8"): consume them
                    // and the final byte, or "B" would leak into the text.
                    while (_index < raw.Length && raw[_index] is >= ' ' and <= '/') _index++;
                    if (_index < raw.Length) _index++;
                    break;
                case 'D' or 'E':
                    // Index / next line.
                    NewLine();
                    break;
                case 'M':
                    // Reverse index (a line above is being rewritten).
                    NewLine();
                    _row = null;
                    break;
                default:
                    // Two-character escapes (ESC 7, ESC 8, ESC =, ESC >, ESC c) end here.
                    break;
            }
        }

        private void SkipString(bool bellEnds)
        {
            while (_index < raw.Length)
            {
                var c = raw[_index++];
                if (bellEnds && c == '\u0007') return;
                if (c == Esc && _index < raw.Length && raw[_index] == '\\')
                {
                    _index++;
                    return;
                }
            }
        }

        /// <summary>A control sequence: parameters, intermediates, then a final byte in @..~.</summary>
        private void Csi()
        {
            var start = _index;
            while (_index < raw.Length && raw[_index] is < '@' or > '~') _index++;
            if (_index >= raw.Length) return;
            var final = raw[_index++];
            var body = raw.AsSpan(start, _index - 1 - start);
            // Private sequences (ESC [ ? 25 h, ESC [ > 0 c) are modes and queries: nothing to show.
            if (body.Length > 0 && body[0] is '?' or '>' or '<' or '=') return;
            switch (final)
            {
                case 'C':
                    // Cursor forward: the console's way of drawing a run of blanks.
                    _out.Append(' ', Math.Min(MaxForward, Parameter(body, 0, 1)));
                    break;
                case 'H' or 'f':
                    MoveTo(Parameter(body, 0, 1), Parameter(body, 1, 1));
                    break;
                case 'd':
                    // Row absolute, column unchanged.
                    MoveTo(Parameter(body, 0, 1), column: null);
                    break;
                case 'G' or '`':
                    // Column absolute on the same row: column 1 is a carriage return.
                    if (Parameter(body, 0, 1) <= 1) _out.Append('\r');
                    break;
                case 'A' or 'F':
                    // Up (to a line already printed): what follows is a rewrite, so it gets a line.
                    NewLine();
                    _row = _row is { } up ? up - Parameter(body, 0, 1) : null;
                    break;
                case 'B' or 'E':
                    NewLine();
                    _row = _row is { } down ? down + Parameter(body, 0, 1) : null;
                    break;
                case 'S' or 'T':
                    // Scrolled: text continues on a fresh line.
                    NewLine();
                    _row = null;
                    break;
                default:
                    // Colours, erase-line/-display, erase-characters, scroll regions, ...
                    break;
            }
        }

        private void MoveTo(int row, int? column)
        {
            if (_row == row)
            {
                // Back to the start of the row being written: overwrite, like a CR.
                if (column is <= 1) _out.Append('\r');
            }
            else
            {
                NewLine();
                _row = row;
            }
        }

        /// <summary>A line break, unless the current line is still empty.</summary>
        private void NewLine()
        {
            if (LineHasText) _out.Append('\n');
        }

        /// <summary>The <paramref name="index"/>th numeric parameter, or <paramref name="fallback"/>
        /// when absent or zero.</summary>
        private static int Parameter(ReadOnlySpan<char> body, int index, int fallback)
        {
            var i = 0;
            foreach (var range in body.Split(';'))
            {
                if (i++ != index) continue;
                var part = body[range];
                return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                    ? n
                    : fallback;
            }
            return fallback;
        }
    }
}
