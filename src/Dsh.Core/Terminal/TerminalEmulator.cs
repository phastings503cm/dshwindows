using System.Text;

namespace Dsh.Core;

public enum TerminalColorKind : byte { Standard, Indexed, Rgb }

/// <summary>A cell colour: the palette default, one of the 256 indexed colours, or 24-bit truecolor.</summary>
public readonly record struct TerminalColor(TerminalColorKind Kind, byte R, byte G, byte B)
{
    public static TerminalColor Standard => default;
    public static TerminalColor Indexed(int index) => new(TerminalColorKind.Indexed, Clamp(index), 0, 0);
    public static TerminalColor Rgb(int r, int g, int b) => new(TerminalColorKind.Rgb, Clamp(r), Clamp(g), Clamp(b));

    /// <summary>The palette index of an indexed colour.</summary>
    public byte Index => R;
    public bool IsStandard => Kind == TerminalColorKind.Standard;

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}

[Flags]
public enum CellAttributes : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Strikethrough = 32,
}

public readonly record struct CellStyle(TerminalColor Foreground, TerminalColor Background, CellAttributes Attributes)
{
    public static CellStyle Normal => default;

    public bool Bold => Has(CellAttributes.Bold);
    public bool Dim => Has(CellAttributes.Dim);
    public bool Italic => Has(CellAttributes.Italic);
    public bool Underline => Has(CellAttributes.Underline);
    public bool Inverse => Has(CellAttributes.Inverse);
    public bool Strikethrough => Has(CellAttributes.Strikethrough);

    public bool Has(CellAttributes flag) => (Attributes & flag) != 0;

    public CellStyle With(CellAttributes flag, bool on) =>
        this with { Attributes = on ? Attributes | flag : Attributes & ~flag };
}

/// <summary>One character cell. <see cref="CodePoint"/> 0 reads as a space.</summary>
public readonly record struct TerminalCell(int CodePoint, CellStyle Style, bool IsContinuation = false)
{
    public static TerminalCell Blank { get; } = new(' ', CellStyle.Normal);

    public bool IsBlank => CodePoint is 0 or ' ';

    public string Text => CodePoint switch
    {
        0 => " ",
        < 0x80 => AsciiStrings[CodePoint],
        _ => char.ConvertFromUtf32(CodePoint),
    };

    private static readonly string[] AsciiStrings = Enumerable.Range(0, 0x80).Select(i => ((char)i).ToString()).ToArray();
}

/// <summary>A VT100/xterm-subset screen.
///
/// It covers what a shell session, a build log, and a TUI-lite program actually emit through
/// ConPTY: cursor motion, erase, insert/delete, scroll regions, SGR colour including 256 and
/// truecolor, the alternate screen, and OSC titles. It is not a full terminal — no sixel, no mouse
/// reporting — and unknown sequences are skipped rather than printed.
///
/// Not thread-safe: callers serialize <see cref="Feed(ReadOnlySpan{byte})"/> against reads.</summary>
public sealed class TerminalEmulator
{
    public int Rows { get; private set; }
    public int Cols { get; private set; }

    private TerminalCell[][] _grid;
    private LineRing _scrollback;

    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public string Title { get; private set; } = "";

    /// <summary>DECCKM: arrow keys send SS3 (ESC O A) instead of CSI.</summary>
    public bool ApplicationCursorKeys { get; private set; }
    /// <summary>The program asked for pastes to be bracketed (ESC [200~ … ESC [201~).</summary>
    public bool BracketedPaste { get; private set; }
    public bool UsingAlternateScreen => _usingAlternate;

    /// <summary>Bumped on every change so the view knows to redraw.</summary>
    public int Revision { get; private set; }
    /// <summary>Bytes the emulator wants to send back (device status reports).</summary>
    public Action<byte[]>? OnReply { get; set; }
    /// <summary>The program rang the bell.</summary>
    public Action? OnBell { get; set; }

    public int ScrollbackLimit
    {
        get => _scrollback.Capacity;
        set => _scrollback.SetCapacity(Math.Max(0, value));
    }

    private CellStyle _style = CellStyle.Normal;
    private (int Row, int Col, CellStyle Style)? _savedCursor;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _autoWrap = true;
    /// <summary>Set once the cursor has printed in the last column, so the wrap happens on the next
    /// character (xterm's deferred-wrap behaviour).</summary>
    private bool _wrapPending;
    private bool _usingAlternate;
    private (TerminalCell[][] Grid, int Row, int Col)? _savedPrimary;
    /// <summary>ESC ( 0 — the DEC line-drawing set.</summary>
    private bool _lineDrawing;
    private int _lastPrinted = ' ';

    // Parser state
    private enum ParseState { Ground, Escape, Csi, Osc, Charset, IgnoreString }
    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _intermediates = new();
    private readonly List<byte> _osc = [];
    private bool _stringEscape;
    private char _charsetTarget;
    private int _utf8Value;
    private int _utf8Needed;
    private int _utf8Min;

    public TerminalEmulator(int rows = 24, int cols = 80, int scrollbackLimit = 5_000)
    {
        Rows = Math.Max(1, rows);
        Cols = Math.Max(1, cols);
        _scrollBottom = Rows - 1;
        _grid = NewGrid(Rows, Cols, TerminalCell.Blank);
        _scrollback = new LineRing(scrollbackLimit);
    }

    // MARK: - Reading

    /// <summary>Lines of history above the screen.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>History plus screen.</summary>
    public int LineCount => _scrollback.Count + Rows;

    /// <summary>A line by index into history-then-screen.</summary>
    public TerminalCell[] Line(int index) =>
        index < _scrollback.Count ? _scrollback[index] : _grid[index - _scrollback.Count];

    /// <summary>The visible screen, row by row.</summary>
    public IReadOnlyList<TerminalCell[]> Screen => _grid;

    /// <summary>Every line, history first — what the view draws and what "copy all" copies.</summary>
    public IEnumerable<TerminalCell[]> AllLines
    {
        get
        {
            for (var i = 0; i < LineCount; i++) yield return Line(i);
        }
    }

    public static string PlainText(TerminalCell[] line) => PlainText(line, 0, line.Length);

    public static string PlainText(TerminalCell[] line, int from, int to)
    {
        var text = new StringBuilder(Math.Max(0, to - from));
        for (var i = Math.Max(0, from); i < Math.Min(to, line.Length); i++)
        {
            if (line[i].IsContinuation) continue;
            text.Append(line[i].Text);
        }
        var end = text.Length;
        while (end > 0 && text[end - 1] == ' ') end--;
        return text.ToString(0, end);
    }

    /// <summary>Everything on screen and in history, as text.</summary>
    public string Transcript => string.Join("\n", AllLines.Select(l => PlainText(l))).Trim();

    /// <summary>Text between two (line, column) positions, end exclusive.</summary>
    public string TextBetween((int Line, int Col) start, (int Line, int Col) end)
    {
        if (end.Line < start.Line || (end.Line == start.Line && end.Col < start.Col)) (start, end) = (end, start);
        var parts = new List<string>();
        for (var index = Math.Max(0, start.Line); index <= end.Line && index < LineCount; index++)
        {
            var line = Line(index);
            var from = index == start.Line ? start.Col : 0;
            var to = index == end.Line ? end.Col : line.Length;
            parts.Add(from < to ? PlainText(line, from, to) : "");
        }
        return string.Join("\n", parts);
    }

    /// <summary>The word around a position, for double-click selection.</summary>
    public (int Start, int End)? WordAt(int lineIndex, int col)
    {
        if (lineIndex < 0 || lineIndex >= LineCount) return null;
        var line = Line(lineIndex);
        if (col < 0 || col >= line.Length || !IsWordCell(line[col])) return null;
        var start = col;
        var end = col;
        while (start > 0 && IsWordCell(line[start - 1])) start--;
        while (end < line.Length && IsWordCell(line[end])) end++;
        return (start, end);
    }

    private static bool IsWordCell(TerminalCell cell)
    {
        if (cell.CodePoint is 0 or ' ') return false;
        if (cell.CodePoint > 0xFFFF) return true;
        var c = (char)cell.CodePoint;
        return char.IsLetterOrDigit(c) || "._-/\\~:@$%+=".Contains(c);
    }

    // MARK: - Feeding

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) Consume(b);
        Revision++;
    }

    private void Consume(byte b)
    {
        switch (_state)
        {
            case ParseState.Ground: Ground(b); break;
            case ParseState.Escape: Escape(b); break;
            case ParseState.Csi: Csi(b); break;
            case ParseState.Osc: Osc(b); break;
            case ParseState.IgnoreString: IgnoreString(b); break;
            case ParseState.Charset:
                if (_charsetTarget == '(') _lineDrawing = b == (byte)'0';
                _state = ParseState.Ground;
                break;
        }
    }

    // MARK: Ground

    private void Ground(byte b)
    {
        // Mid-way through a multi-byte character?
        if (_utf8Needed > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8Value = (_utf8Value << 6) | (b & 0x3F);
                if (--_utf8Needed == 0)
                {
                    var cp = _utf8Value;
                    if (cp < _utf8Min || cp > 0x10FFFF || cp is >= 0xD800 and <= 0xDFFF) cp = 0xFFFD;
                    Put(cp);
                }
                return;
            }
            // Invalid continuation: drop what we had and reprocess this byte.
            _utf8Needed = 0;
        }

        switch (b)
        {
            case 0x07: OnBell?.Invoke(); break;
            case 0x08: Backspace(); break;
            case 0x09: Tab(1); break;
            case 0x0A or 0x0B or 0x0C: LineFeed(); break;
            case 0x0D: CursorCol = 0; _wrapPending = false; break;
            case 0x1B: EnterEscape(); break;
            case < 0x20: break;
            case < 0x7F: Put(b); break;
            case 0x7F: break;
            default:
                // Start of a multi-byte UTF-8 sequence.
                if ((b & 0xE0) == 0xC0) { _utf8Value = b & 0x1F; _utf8Needed = 1; _utf8Min = 0x80; }
                else if ((b & 0xF0) == 0xE0) { _utf8Value = b & 0x0F; _utf8Needed = 2; _utf8Min = 0x800; }
                else if ((b & 0xF8) == 0xF0) { _utf8Value = b & 0x07; _utf8Needed = 3; _utf8Min = 0x10000; }
                break;
        }
    }

    private void EnterEscape()
    {
        _state = ParseState.Escape;
        _params.Clear();
        _intermediates.Clear();
    }

    /// <summary>DEC special graphics for 0x5F–0x7E: the box-drawing set TUIs use under ESC ( 0.</summary>
    private static readonly string LineDrawingMap =
        "\u00A0◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private void Put(int codePoint)
    {
        if (_lineDrawing && codePoint is >= 0x5F and <= 0x7E) codePoint = LineDrawingMap[codePoint - 0x5F];
        _lastPrinted = codePoint;
        if (_wrapPending && _autoWrap)
        {
            CursorCol = 0;
            LineFeed();
            _wrapPending = false;
        }
        if (CursorRow < 0 || CursorRow >= Rows) return;
        if (CursorCol >= Cols)
        {
            if (!_autoWrap) return;
            CursorCol = 0;
            LineFeed();
        }
        var width = Width(codePoint);
        if (width == 0)
        {
            // A combining mark or zero-width joiner: it belongs to the previous cell, which we
            // cannot compose into a single code point, so it is dropped rather than given a column.
            return;
        }
        if (width == 2 && CursorCol == Cols - 1)
        {
            // A wide glyph will not fit: wrap it whole rather than splitting.
            if (!_autoWrap) return;
            _grid[CursorRow][CursorCol] = new TerminalCell(' ', _style);
            CursorCol = 0;
            LineFeed();
        }
        _grid[CursorRow][CursorCol] = new TerminalCell(codePoint, _style);
        CursorCol++;
        if (width == 2 && CursorCol < Cols)
        {
            _grid[CursorRow][CursorCol] = new TerminalCell(' ', _style, IsContinuation: true);
            CursorCol++;
        }
        if (CursorCol >= Cols)
        {
            CursorCol = Cols - 1;
            _wrapPending = true;
        }
    }

    private void Backspace()
    {
        _wrapPending = false;
        if (CursorCol > 0) CursorCol--;
    }

    private void Tab(int count)
    {
        _wrapPending = false;
        for (var i = 0; i < count; i++) CursorCol = Math.Min((CursorCol / 8 + 1) * 8, Cols - 1);
    }

    private void BackTab(int count)
    {
        _wrapPending = false;
        for (var i = 0; i < count; i++) CursorCol = Math.Max(0, (CursorCol - 1) / 8 * 8);
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (CursorRow == _scrollBottom) ScrollUp(1);
        else if (CursorRow < Rows - 1) CursorRow++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorRow == _scrollTop) ScrollDown(1);
        else if (CursorRow > 0) CursorRow--;
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (var n = 0; n < count; n++)
        {
            var line = _grid[_scrollTop];
            // Only the primary screen's history is worth keeping; the alternate screen is a
            // scratch surface by definition.
            if (!_usingAlternate && _scrollTop == 0) _scrollback.Add(line);
            Array.Copy(_grid, _scrollTop + 1, _grid, _scrollTop, _scrollBottom - _scrollTop);
            _grid[_scrollBottom] = BlankRow();
        }
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        for (var n = 0; n < count; n++)
        {
            Array.Copy(_grid, _scrollTop, _grid, _scrollTop + 1, _scrollBottom - _scrollTop);
            _grid[_scrollTop] = BlankRow();
        }
    }

    // MARK: Escape

    private void Escape(byte b)
    {
        _state = ParseState.Ground;
        switch ((char)b)
        {
            case '[': _state = ParseState.Csi; _params.Clear(); _intermediates.Clear(); break;
            case ']': _state = ParseState.Osc; _osc.Clear(); _stringEscape = false; break;
            case 'P' or 'X' or '^' or '_': _state = ParseState.IgnoreString; _stringEscape = false; break;
            case '(' or ')' or '*' or '+': _state = ParseState.Charset; _charsetTarget = (char)b; break;
            case '7': _savedCursor = (CursorRow, CursorCol, _style); break;
            case '8': RestoreCursor(); break;
            case 'M': ReverseIndex(); break;
            case 'D': LineFeed(); break;
            case 'E': CursorCol = 0; LineFeed(); break;
            case 'c': Reset(); break;
            case '\\': break; // a stray string terminator
            case '\x1B': EnterEscape(); break;
            default: break; // ESC = / ESC > (keypad modes) and anything unknown
        }
    }

    private void RestoreCursor()
    {
        if (_savedCursor is not { } saved) return;
        CursorRow = Math.Min(saved.Row, Rows - 1);
        CursorCol = Math.Min(saved.Col, Cols - 1);
        _style = saved.Style;
        _wrapPending = false;
    }

    // MARK: OSC and ignored strings

    private void Osc(byte b)
    {
        if (_stringEscape)
        {
            _stringEscape = false;
            FinishOsc();
            // ESC \ is the terminator; any other byte after ESC starts a new sequence.
            if (b != (byte)'\\') { EnterEscape(); Escape(b); }
            return;
        }
        switch (b)
        {
            case 0x07 or 0x9C: FinishOsc(); return;
            case 0x1B: _stringEscape = true; return;
        }
        if (_osc.Count < 2048) _osc.Add(b);
    }

    private void FinishOsc()
    {
        var text = Encoding.UTF8.GetString(_osc.ToArray());
        var semicolon = text.IndexOf(';');
        if (semicolon > 0 && text[..semicolon] is "0" or "2") Title = text[(semicolon + 1)..];
        _osc.Clear();
        _state = ParseState.Ground;
    }

    /// <summary>DCS, SOS, PM, APC: nothing we act on, but their payload must not print.</summary>
    private void IgnoreString(byte b)
    {
        if (_stringEscape)
        {
            _stringEscape = false;
            _state = ParseState.Ground;
            if (b != (byte)'\\') { EnterEscape(); Escape(b); }
            return;
        }
        if (b == 0x1B) _stringEscape = true;
        else if (b is 0x07 or 0x9C) _state = ParseState.Ground;
    }

    // MARK: CSI

    private void Csi(byte b)
    {
        // Parameters and intermediates accumulate until a final byte arrives.
        if (b is >= 0x30 and <= 0x3F)
        {
            if (_params.Length < 128) _params.Append((char)b);
            return;
        }
        if (b is >= 0x20 and <= 0x2F)
        {
            if (_intermediates.Length < 8) _intermediates.Append((char)b);
            return;
        }
        if (b == 0x1B)
        {
            EnterEscape();
            return;
        }
        if (b is < 0x40 or > 0x7E)
        {
            // A control character inside a sequence executes, as on a real terminal.
            if (b < 0x20) Ground(b);
            else _state = ParseState.Ground;
            return;
        }
        _state = ParseState.Ground;
        if (_intermediates.Length > 0) return; // DECSCUSR, DECSTR and friends: not ours to act on

        var raw = _params.ToString();
        var marker = raw.Length > 0 && raw[0] is '?' or '>' or '=' or '<' ? raw[0] : '\0';
        var body = marker == '\0' ? raw : raw[1..];
        var groups = ParseParams(body);

        int P(int index, int fallback = 1) =>
            index < groups.Count && groups[index].Length > 0 && groups[index][0] > 0 ? groups[index][0] : fallback;

        if (marker is '>' or '=' or '<')
        {
            // Secondary device attributes is the only one worth answering; "> 4;1 m" and the like
            // (modifyOtherKeys) must not be read as colours.
            if (b == (byte)'c' && marker == '>') OnReply?.Invoke(Encoding.ASCII.GetBytes("\x1B[>0;10;1c"));
            return;
        }
        if (marker == '?')
        {
            switch ((char)b)
            {
                case 'h': SetPrivateModes(groups, true); break;
                case 'l': SetPrivateModes(groups, false); break;
                case 'J': EraseInDisplay(P(0, 0)); break;
                case 'K': EraseInLine(P(0, 0)); break;
            }
            return;
        }

        switch ((char)b)
        {
            case 'A': CursorRow = Math.Max(CursorRow >= _scrollTop ? _scrollTop : 0, CursorRow - P(0)); _wrapPending = false; break;
            case 'B' or 'e': CursorRow = Math.Min(CursorRow <= _scrollBottom ? _scrollBottom : Rows - 1, CursorRow + P(0)); _wrapPending = false; break;
            case 'C' or 'a': CursorCol = Math.Min(Cols - 1, CursorCol + P(0)); _wrapPending = false; break;
            case 'D': CursorCol = Math.Max(0, CursorCol - P(0)); _wrapPending = false; break;
            case 'E': CursorRow = Math.Min(_scrollBottom, CursorRow + P(0)); CursorCol = 0; _wrapPending = false; break;
            case 'F': CursorRow = Math.Max(_scrollTop, CursorRow - P(0)); CursorCol = 0; _wrapPending = false; break;
            case 'G' or '`': CursorCol = ClampCol(P(0) - 1); _wrapPending = false; break;
            case 'd': CursorRow = ClampRow(P(0) - 1); _wrapPending = false; break;
            case 'H' or 'f':
                CursorRow = ClampRow(P(0) - 1);
                CursorCol = ClampCol(P(1) - 1);
                _wrapPending = false;
                break;
            case 'I': Tab(P(0)); break;
            case 'Z': BackTab(P(0)); break;
            case 'J': EraseInDisplay(P(0, 0)); break;
            case 'K': EraseInLine(P(0, 0)); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'P': DeleteCharacters(P(0)); break;
            case '@': InsertCharacters(P(0)); break;
            case 'X': EraseCharacters(P(0)); break;
            case 'S': ScrollUp(P(0)); break;
            case 'T': ScrollDown(P(0)); break;
            case 'b':
                // REP: repeat the last printed character.
                for (var i = Math.Min(P(0), Rows * Cols); i > 0; i--) Put(_lastPrinted);
                break;
            case 'm': ApplySgr(groups.Count == 0 ? [[0]] : groups); break;
            case 'r':
            {
                var top = ClampRow(P(0) - 1);
                var bottom = ClampRow(P(1, Rows) - 1);
                if (top < bottom)
                {
                    _scrollTop = top;
                    _scrollBottom = bottom;
                }
                CursorRow = 0;
                CursorCol = 0;
                _wrapPending = false;
                break;
            }
            case 's': _savedCursor = (CursorRow, CursorCol, _style); break;
            case 'u': RestoreCursor(); break;
            case 'n':
                if (P(0, 0) == 6) OnReply?.Invoke(Encoding.ASCII.GetBytes($"\x1B[{CursorRow + 1};{CursorCol + 1}R"));
                else if (P(0, 0) == 5) OnReply?.Invoke(Encoding.ASCII.GetBytes("\x1B[0n"));
                break;
            case 'c':
                if (P(0, 0) == 0) OnReply?.Invoke(Encoding.ASCII.GetBytes("\x1B[?1;2c")); // "a VT100 with AVO"
                break;
            default:
                break; // window ops (t), insert mode (h/l without ?), and anything else
        }
    }

    /// <summary>"1;38:2::10:20:30" → [[1], [38, 2, 0, 10, 20, 30]]. Empty parameters read as 0.</summary>
    private static List<int[]> ParseParams(string body)
    {
        var groups = new List<int[]>();
        if (body.Length == 0) return groups;
        foreach (var group in body.Split(';'))
        {
            var parts = group.Split(':');
            var values = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                values[i] = int.TryParse(parts[i], out var v) ? Math.Min(v, 65_535) : 0;
            groups.Add(values);
        }
        return groups;
    }

    private int ClampRow(int value) => Math.Clamp(value, 0, Rows - 1);
    private int ClampCol(int value) => Math.Clamp(value, 0, Cols - 1);

    private void SetPrivateModes(List<int[]> groups, bool enabled)
    {
        foreach (var group in groups)
        {
            switch (group.Length > 0 ? group[0] : 0)
            {
                case 1: ApplicationCursorKeys = enabled; break;
                case 7: _autoWrap = enabled; if (!enabled) _wrapPending = false; break;
                case 25: CursorVisible = enabled; break;
                case 47 or 1047: SetAlternateScreen(enabled, saveCursor: false); break;
                case 1049: SetAlternateScreen(enabled, saveCursor: true); break;
                case 2004: BracketedPaste = enabled; break;
                default: break; // mouse reporting, focus events, win32-input-mode, cursor blink
            }
        }
    }

    private void SetAlternateScreen(bool on, bool saveCursor)
    {
        if (on == _usingAlternate) return;
        if (on)
        {
            if (saveCursor) _savedCursor = (CursorRow, CursorCol, _style);
            _savedPrimary = (_grid, CursorRow, CursorCol);
            _grid = NewGrid(Rows, Cols, TerminalCell.Blank);
            CursorRow = 0;
            CursorCol = 0;
            _usingAlternate = true;
        }
        else
        {
            if (_savedPrimary is { } saved)
            {
                _grid = FitGrid(saved.Grid, Rows, Cols);
                CursorRow = Math.Min(saved.Row, Rows - 1);
                CursorCol = Math.Min(saved.Col, Cols - 1);
            }
            _savedPrimary = null;
            _usingAlternate = false;
            if (saveCursor) RestoreCursor();
        }
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _wrapPending = false;
        _style = CellStyle.Normal;
    }

    // MARK: Erase / insert / delete

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var row = CursorRow + 1; row < Rows; row++) _grid[row] = BlankRow();
                break;
            case 1:
                EraseInLine(1);
                for (var row = 0; row < CursorRow; row++) _grid[row] = BlankRow();
                break;
            case 2:
                for (var row = 0; row < Rows; row++) _grid[row] = BlankRow();
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        if (CursorRow >= Rows) return;
        var line = _grid[CursorRow];
        var blank = BlankCell();
        switch (mode)
        {
            case 0: Array.Fill(line, blank, CursorCol, Cols - CursorCol); break;
            case 1: Array.Fill(line, blank, 0, Math.Min(CursorCol + 1, Cols)); break;
            case 2: Array.Fill(line, blank); break;
        }
        _wrapPending = false;
    }

    private void EraseCharacters(int count)
    {
        if (CursorRow >= Rows) return;
        Array.Fill(_grid[CursorRow], BlankCell(), CursorCol, Math.Min(count, Cols - CursorCol));
        _wrapPending = false;
    }

    private void InsertLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (var n = 0; n < count; n++)
        {
            Array.Copy(_grid, CursorRow, _grid, CursorRow + 1, _scrollBottom - CursorRow);
            _grid[CursorRow] = BlankRow();
        }
        CursorCol = 0;
        _wrapPending = false;
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (var n = 0; n < count; n++)
        {
            Array.Copy(_grid, CursorRow + 1, _grid, CursorRow, _scrollBottom - CursorRow);
            _grid[_scrollBottom] = BlankRow();
        }
        CursorCol = 0;
        _wrapPending = false;
    }

    private void InsertCharacters(int count)
    {
        if (CursorRow >= Rows) return;
        var line = _grid[CursorRow];
        count = Math.Min(count, Cols - CursorCol);
        Array.Copy(line, CursorCol, line, CursorCol + count, Cols - CursorCol - count);
        Array.Fill(line, BlankCell(), CursorCol, count);
        _wrapPending = false;
    }

    private void DeleteCharacters(int count)
    {
        if (CursorRow >= Rows) return;
        var line = _grid[CursorRow];
        count = Math.Min(count, Cols - CursorCol);
        Array.Copy(line, CursorCol + count, line, CursorCol, Cols - CursorCol - count);
        Array.Fill(line, BlankCell(), Cols - count, count);
        _wrapPending = false;
    }

    /// <summary>Erased cells keep the current background, which is what makes a coloured clear fill
    /// the screen rather than leave stripes.</summary>
    private TerminalCell BlankCell() =>
        _style.Background.IsStandard ? TerminalCell.Blank : new TerminalCell(' ', new CellStyle(TerminalColor.Standard, _style.Background, CellAttributes.None));

    private TerminalCell[] BlankRow()
    {
        var row = new TerminalCell[Cols];
        Array.Fill(row, BlankCell());
        return row;
    }

    private static TerminalCell[][] NewGrid(int rows, int cols, TerminalCell fill)
    {
        var grid = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            grid[r] = new TerminalCell[cols];
            Array.Fill(grid[r], fill);
        }
        return grid;
    }

    // MARK: SGR

    private void ApplySgr(List<int[]> groups)
    {
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var code = group.Length > 0 ? group[0] : 0;
            switch (code)
            {
                case 0: _style = CellStyle.Normal; break;
                case 1: _style = _style.With(CellAttributes.Bold, true); break;
                case 2: _style = _style.With(CellAttributes.Dim, true); break;
                case 3: _style = _style.With(CellAttributes.Italic, true); break;
                case 4: _style = _style.With(CellAttributes.Underline, !(group.Length > 1 && group[1] == 0)); break;
                case 7: _style = _style.With(CellAttributes.Inverse, true); break;
                case 9: _style = _style.With(CellAttributes.Strikethrough, true); break;
                case 21 or 22: _style = _style.With(CellAttributes.Bold, false).With(CellAttributes.Dim, false); break;
                case 23: _style = _style.With(CellAttributes.Italic, false); break;
                case 24: _style = _style.With(CellAttributes.Underline, false); break;
                case 27: _style = _style.With(CellAttributes.Inverse, false); break;
                case 29: _style = _style.With(CellAttributes.Strikethrough, false); break;
                case >= 30 and <= 37: _style = _style with { Foreground = TerminalColor.Indexed(code - 30) }; break;
                case 39: _style = _style with { Foreground = TerminalColor.Standard }; break;
                case >= 40 and <= 47: _style = _style with { Background = TerminalColor.Indexed(code - 40) }; break;
                case 49: _style = _style with { Background = TerminalColor.Standard }; break;
                case >= 90 and <= 97: _style = _style with { Foreground = TerminalColor.Indexed(code - 90 + 8) }; break;
                case >= 100 and <= 107: _style = _style with { Background = TerminalColor.Indexed(code - 100 + 8) }; break;
                case 38 or 48:
                {
                    TerminalColor? colour;
                    if (group.Length > 1)
                    {
                        colour = ColonColour(group);
                    }
                    else
                    {
                        (colour, var consumed) = SemicolonColour(groups, index);
                        index += consumed;
                    }
                    if (colour is { } c)
                        _style = code == 38 ? _style with { Foreground = c } : _style with { Background = c };
                    break;
                }
            }
        }
    }

    /// <summary><c>38;5;n</c> (indexed) and <c>38;2;r;g;b</c> (truecolor).</summary>
    private static (TerminalColor? Colour, int Consumed) SemicolonColour(List<int[]> groups, int index)
    {
        int At(int i) => groups[i].Length > 0 ? groups[i][0] : 0;
        if (index + 1 >= groups.Count) return (null, 0);
        switch (At(index + 1))
        {
            case 5:
                return index + 2 < groups.Count ? (TerminalColor.Indexed(At(index + 2)), 2) : (null, 1);
            case 2:
                return index + 4 < groups.Count
                    ? (TerminalColor.Rgb(At(index + 2), At(index + 3), At(index + 4)), 4)
                    : (null, groups.Count - index - 1);
            default:
                return (null, 1);
        }
    }

    /// <summary><c>38:5:n</c> and <c>38:2:[colourspace]:r:g:b</c>.</summary>
    private static TerminalColor? ColonColour(int[] group)
    {
        switch (group[1])
        {
            case 5 when group.Length > 2: return TerminalColor.Indexed(group[2]);
            case 2 when group.Length >= 6: return TerminalColor.Rgb(group[3], group[4], group[5]);
            case 2 when group.Length == 5: return TerminalColor.Rgb(group[2], group[3], group[4]);
            default: return null;
        }
    }

    // MARK: - Geometry

    public void Resize(int rows, int cols)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        if (rows == Rows && cols == Cols) return;

        var lines = _grid.Select(line => FitLine(line, cols)).ToList();
        if (lines.Count > rows)
        {
            // Growing shorter pushes the top into scrollback so output above the fold is not
            // simply lost. Only the rows above the cursor go: blank rows below it are dropped first.
            var excess = lines.Count - rows;
            var trailing = 0;
            for (var r = lines.Count - 1; r > CursorRow && trailing < excess && IsBlankLine(lines[r]); r--) trailing++;
            lines.RemoveRange(lines.Count - trailing, trailing);
            var dropped = lines.Count - rows;
            if (dropped > 0)
            {
                if (!_usingAlternate)
                    foreach (var line in lines.Take(dropped)) _scrollback.Add(line);
                lines.RemoveRange(0, dropped);
                CursorRow = Math.Max(0, CursorRow - dropped);
            }
        }
        while (lines.Count < rows)
        {
            var blank = new TerminalCell[cols];
            Array.Fill(blank, TerminalCell.Blank);
            lines.Add(blank);
        }
        _grid = lines.ToArray();
        if (_savedPrimary is { } saved) _savedPrimary = (FitGrid(saved.Grid, rows, cols), Math.Min(saved.Row, rows - 1), Math.Min(saved.Col, cols - 1));
        Rows = rows;
        Cols = cols;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        CursorRow = Math.Min(CursorRow, rows - 1);
        CursorCol = Math.Min(CursorCol, cols - 1);
        _wrapPending = false;
        Revision++;
    }

    private static bool IsBlankLine(TerminalCell[] line)
    {
        foreach (var cell in line)
            if (!cell.IsBlank || !cell.Style.Background.IsStandard) return false;
        return true;
    }

    private static TerminalCell[] FitLine(TerminalCell[] line, int cols)
    {
        if (line.Length == cols) return line;
        var fitted = new TerminalCell[cols];
        Array.Copy(line, fitted, Math.Min(cols, line.Length));
        if (cols > line.Length) Array.Fill(fitted, TerminalCell.Blank, line.Length, cols - line.Length);
        return fitted;
    }

    private static TerminalCell[][] FitGrid(TerminalCell[][] grid, int rows, int cols)
    {
        var fitted = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            if (r < grid.Length) fitted[r] = FitLine(grid[r], cols);
            else
            {
                fitted[r] = new TerminalCell[cols];
                Array.Fill(fitted[r], TerminalCell.Blank);
            }
        }
        return fitted;
    }

    public void Reset()
    {
        _grid = NewGrid(Rows, Cols, TerminalCell.Blank);
        _scrollback.Clear();
        CursorRow = 0;
        CursorCol = 0;
        _style = CellStyle.Normal;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _usingAlternate = false;
        _savedPrimary = null;
        _savedCursor = null;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        _autoWrap = true;
        _wrapPending = false;
        _lineDrawing = false;
        Revision++;
    }

    /// <summary>Columns a code point occupies: 2 for East Asian wide and emoji, 0 for combining marks.</summary>
    public static int Width(int codePoint)
    {
        if (codePoint < 0x300) return 1;
        if (codePoint is (>= 0x300 and <= 0x36F) or (>= 0x1AB0 and <= 0x1AFF) or (>= 0x1DC0 and <= 0x1DFF)
            or (>= 0x20D0 and <= 0x20FF) or (>= 0xFE20 and <= 0xFE2F) or 0x200B or 0x200C or 0x200D or 0x2060
            or (>= 0xFE00 and <= 0xFE0F))
            return 0;
        return codePoint switch
        {
            >= 0x1100 and <= 0x115F => 2,
            >= 0x2E80 and <= 0x303E => 2,
            >= 0x3041 and <= 0x33FF => 2,
            >= 0x3400 and <= 0x4DBF => 2,
            >= 0x4E00 and <= 0x9FFF => 2,
            >= 0xA000 and <= 0xA4CF => 2,
            >= 0xAC00 and <= 0xD7A3 => 2,
            >= 0xF900 and <= 0xFAFF => 2,
            >= 0xFE30 and <= 0xFE6F => 2,
            >= 0xFF00 and <= 0xFF60 => 2,
            >= 0xFFE0 and <= 0xFFE6 => 2,
            >= 0x1F300 and <= 0x1F64F => 2,
            >= 0x1F900 and <= 0x1F9FF => 2,
            >= 0x20000 and <= 0x3FFFD => 2,
            _ => 1,
        };
    }

    /// <summary>Fixed-capacity history: the oldest line falls off when a new one arrives.</summary>
    private sealed class LineRing(int capacity)
    {
        private TerminalCell[][] _items = new TerminalCell[Math.Max(0, capacity)][];
        private int _start;

        public int Count { get; private set; }
        public int Capacity => _items.Length;

        public TerminalCell[] this[int index] => _items[(_start + index) % _items.Length];

        public void Add(TerminalCell[] line)
        {
            if (_items.Length == 0) return;
            if (Count < _items.Length)
            {
                _items[(_start + Count) % _items.Length] = line;
                Count++;
            }
            else
            {
                _items[_start] = line;
                _start = (_start + 1) % _items.Length;
            }
        }

        public void Clear()
        {
            Array.Clear(_items);
            _start = 0;
            Count = 0;
        }

        public void SetCapacity(int capacity)
        {
            if (capacity == _items.Length) return;
            var keep = Math.Min(Count, capacity);
            var next = new TerminalCell[capacity][];
            for (var i = 0; i < keep; i++) next[i] = this[Count - keep + i];
            _items = next;
            _start = 0;
            Count = keep;
        }
    }
}
