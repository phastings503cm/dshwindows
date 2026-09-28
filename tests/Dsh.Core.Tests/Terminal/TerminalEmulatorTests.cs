using System.Text;

namespace Dsh.Core.Tests.Terminal;

/// <summary>The emulator is the piece with the most state, so it gets the most tests.</summary>
public class TerminalEmulatorTests
{
    private static TerminalEmulator Make(int rows = 6, int cols = 20) => new(rows, cols);

    private static string Line(TerminalEmulator emulator, int index) => TerminalEmulator.PlainText(emulator.Screen[index]);

    private const string Esc = "\u001B";

    // MARK: Printing

    [Fact]
    public void PrintsPlainText()
    {
        var emulator = Make();
        emulator.Feed("hello");
        Assert.Equal("hello", Line(emulator, 0));
        Assert.Equal(5, emulator.CursorCol);
    }

    [Fact]
    public void CarriageReturnAndLineFeed()
    {
        var emulator = Make();
        emulator.Feed("one\r\ntwo");
        Assert.Equal("one", Line(emulator, 0));
        Assert.Equal("two", Line(emulator, 1));
        Assert.Equal(1, emulator.CursorRow);
    }

    [Fact]
    public void CarriageReturnOverwritesInPlace()
    {
        var emulator = Make();
        emulator.Feed("abcdef\rXY");
        Assert.Equal("XYcdef", Line(emulator, 0));
    }

    [Fact]
    public void BackspaceMovesWithoutErasing()
    {
        var emulator = Make();
        emulator.Feed("abc\b\bZ");
        Assert.Equal("aZc", Line(emulator, 0));
    }

    [Fact]
    public void WrapsAtRightMargin()
    {
        var emulator = Make(4, 5);
        emulator.Feed("abcdefgh");
        Assert.Equal("abcde", Line(emulator, 0));
        Assert.Equal("fgh", Line(emulator, 1));
    }

    /// <summary>xterm defers the wrap until the next character, so a line that ends exactly at the
    /// margin must not eat a blank row.</summary>
    [Fact]
    public void DeferredWrapDoesNotInsertBlankLine()
    {
        var emulator = Make(4, 5);
        emulator.Feed("abcde\r\nxy");
        Assert.Equal("abcde", Line(emulator, 0));
        Assert.Equal("xy", Line(emulator, 1));
    }

    [Fact]
    public void TabStops()
    {
        var emulator = Make(2, 30);
        emulator.Feed("a\tb\tc");
        Assert.Equal("a       b       c", Line(emulator, 0));
    }

    [Fact]
    public void Utf8AcrossChunkBoundary()
    {
        var emulator = Make();
        var bytes = Encoding.UTF8.GetBytes("h\u00E9llo");
        // Split mid-character: 'é' is two bytes.
        emulator.Feed(bytes.AsSpan(0, 2));
        emulator.Feed(bytes.AsSpan(2));
        Assert.Equal("h\u00E9llo", Line(emulator, 0));
    }

    [Fact]
    public void FourByteCharactersSurviveChunking()
    {
        var emulator = Make();
        var bytes = Encoding.UTF8.GetBytes("a\U0001F600b");
        foreach (var b in bytes) emulator.Feed([b]);
        Assert.Equal("a\U0001F600b", Line(emulator, 0));
        // The emoji is wide.
        Assert.Equal(4, emulator.CursorCol);
    }

    [Fact]
    public void InvalidUtf8IsReplacedNotFatal()
    {
        var emulator = Make();
        emulator.Feed([(byte)'a', 0xC3, (byte)'b']);
        Assert.StartsWith("a", Line(emulator, 0));
        Assert.EndsWith("b", Line(emulator, 0));
    }

    // MARK: Cursor motion

    [Fact]
    public void CursorPositionAbsolute()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[3;5Hx");
        Assert.Equal(2, emulator.CursorRow);
        Assert.Equal("    x", Line(emulator, 2));
    }

    [Fact]
    public void CursorRelativeMoves()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[2B{Esc}[3Cx");
        Assert.Equal(2, emulator.CursorRow);
        Assert.Equal("   x", Line(emulator, 2));
    }

    [Fact]
    public void CursorMovesClampToScreen()
    {
        var emulator = Make(4, 10);
        emulator.Feed($"{Esc}[99B{Esc}[99C");
        Assert.Equal(3, emulator.CursorRow);
        Assert.Equal(9, emulator.CursorCol);
    }

    [Fact]
    public void SaveAndRestoreCursor()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[2;3H{Esc}7{Esc}[5;9H{Esc}8x");
        Assert.Equal(1, emulator.CursorRow);
        Assert.Equal("  x", Line(emulator, 1));
    }

    // MARK: Erase

    [Fact]
    public void EraseToEndOfLine()
    {
        var emulator = Make();
        emulator.Feed($"abcdef{Esc}[3G{Esc}[K");
        Assert.Equal("ab", Line(emulator, 0));
    }

    [Fact]
    public void EraseWholeDisplay()
    {
        var emulator = Make();
        emulator.Feed($"one\r\ntwo{Esc}[2J");
        Assert.Equal("", Line(emulator, 0));
        Assert.Equal("", Line(emulator, 1));
    }

    [Fact]
    public void EraseBelowLeavesEarlierLines()
    {
        var emulator = Make();
        emulator.Feed($"one\r\ntwo\r\nthree{Esc}[2;1H{Esc}[J");
        Assert.Equal("one", Line(emulator, 0));
        Assert.Equal("", Line(emulator, 1));
        Assert.Equal("", Line(emulator, 2));
    }

    [Fact]
    public void EraseKeepsCurrentBackground()
    {
        var emulator = Make(2, 4);
        emulator.Feed($"{Esc}[44m{Esc}[2J");
        Assert.Equal(TerminalColor.Indexed(4), emulator.Screen[1][3].Style.Background);
    }

    [Fact]
    public void DeleteAndInsertCharacters()
    {
        var emulator = Make();
        emulator.Feed($"abcdef{Esc}[1G{Esc}[2P");
        Assert.Equal("cdef", Line(emulator, 0));
        emulator.Feed($"{Esc}[1G{Esc}[2@");
        Assert.Equal("  cdef", Line(emulator, 0));
    }

    [Fact]
    public void EraseCharactersLeavesTheRest()
    {
        var emulator = Make();
        emulator.Feed($"abcdef{Esc}[2G{Esc}[2X");
        Assert.Equal("a  def", Line(emulator, 0));
    }

    [Fact]
    public void InsertAndDeleteLines()
    {
        var emulator = Make(4, 10);
        emulator.Feed($"a\r\nb\r\nc{Esc}[1;1H{Esc}[L");
        Assert.Equal("", Line(emulator, 0));
        Assert.Equal("a", Line(emulator, 1));
        emulator.Feed($"{Esc}[1;1H{Esc}[M");
        Assert.Equal("a", Line(emulator, 0));
    }

    // MARK: Scrolling

    [Fact]
    public void ScrollPushesIntoScrollback()
    {
        var emulator = Make(3, 10);
        emulator.Feed("1\r\n2\r\n3\r\n4");
        Assert.Equal(1, emulator.ScrollbackCount);
        Assert.Equal("1", TerminalEmulator.PlainText(emulator.Line(0)));
        Assert.Equal("4", Line(emulator, 2));
    }

    [Fact]
    public void ScrollRegionKeepsOutsideLines()
    {
        var emulator = Make(5, 10);
        emulator.Feed("top\r\na\r\nb\r\nc\r\nbottom");
        // Confine scrolling to rows 2–4, then force a scroll inside it.
        emulator.Feed($"{Esc}[2;4r{Esc}[4;1H\r\nnew");
        Assert.Equal("top", Line(emulator, 0));
        Assert.Equal("bottom", Line(emulator, 4));
        Assert.Equal("new", Line(emulator, 3));
    }

    [Fact]
    public void ReverseIndexScrollsDown()
    {
        var emulator = Make(3, 10);
        emulator.Feed($"a\r\nb{Esc}[1;1H{Esc}M");
        Assert.Equal("", Line(emulator, 0));
        Assert.Equal("a", Line(emulator, 1));
    }

    [Fact]
    public void ScrollbackIsCapped()
    {
        var emulator = Make(2, 10);
        emulator.ScrollbackLimit = 5;
        for (var index = 0; index < 40; index++) emulator.Feed($"{index}\r\n");
        Assert.True(emulator.ScrollbackCount <= 5);
        // The newest history survives, oldest first.
        Assert.Equal("34", TerminalEmulator.PlainText(emulator.Line(0)));
        Assert.Equal("38", TerminalEmulator.PlainText(emulator.Line(emulator.ScrollbackCount - 1)));
    }

    [Fact]
    public void ClearScrollbackSequence()
    {
        var emulator = Make(2, 10);
        emulator.Feed("1\r\n2\r\n3\r\n4");
        Assert.True(emulator.ScrollbackCount > 0);
        emulator.Feed($"{Esc}[3J");
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    // MARK: SGR

    [Fact]
    public void BasicColourAndReset()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[31mred{Esc}[0mplain");
        Assert.Equal(TerminalColor.Indexed(1), emulator.Screen[0][0].Style.Foreground);
        Assert.Equal(TerminalColor.Standard, emulator.Screen[0][3].Style.Foreground);
    }

    [Fact]
    public void BrightColoursAndBackground()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[92;44mx");
        Assert.Equal(TerminalColor.Indexed(10), emulator.Screen[0][0].Style.Foreground);
        Assert.Equal(TerminalColor.Indexed(4), emulator.Screen[0][0].Style.Background);
    }

    [Fact]
    public void Colour256AndTruecolor()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[38;5;123mA{Esc}[38;2;10;20;30mB");
        Assert.Equal(TerminalColor.Indexed(123), emulator.Screen[0][0].Style.Foreground);
        Assert.Equal(TerminalColor.Rgb(10, 20, 30), emulator.Screen[0][1].Style.Foreground);
    }

    [Fact]
    public void ColonSeparatedTruecolor()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[38:2::1:2:3mA{Esc}[48:5:200mB");
        Assert.Equal(TerminalColor.Rgb(1, 2, 3), emulator.Screen[0][0].Style.Foreground);
        Assert.Equal(TerminalColor.Indexed(200), emulator.Screen[0][1].Style.Background);
    }

    [Fact]
    public void AttributesToggleIndependently()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[1;4mA{Esc}[24mB");
        Assert.True(emulator.Screen[0][0].Style.Bold);
        Assert.True(emulator.Screen[0][0].Style.Underline);
        Assert.True(emulator.Screen[0][1].Style.Bold);
        Assert.False(emulator.Screen[0][1].Style.Underline);
    }

    [Fact]
    public void PrivateMarkerSequencesAreNotColours()
    {
        // vim sends "CSI > 4 ; 2 m" (modifyOtherKeys); it must not turn on underline and dim.
        var emulator = Make();
        emulator.Feed($"{Esc}[>4;2mA");
        Assert.Equal(CellStyle.Normal, emulator.Screen[0][0].Style);
    }

    // MARK: Modes

    [Fact]
    public void AlternateScreenRoundTrips()
    {
        var emulator = Make();
        emulator.Feed("primary");
        emulator.Feed($"{Esc}[?1049h");
        Assert.Equal("", Line(emulator, 0));
        emulator.Feed("alt");
        Assert.Equal("alt", Line(emulator, 0));
        emulator.Feed($"{Esc}[?1049l");
        Assert.Equal("primary", Line(emulator, 0));
        Assert.False(emulator.UsingAlternateScreen);
    }

    [Fact]
    public void AlternateScreenDoesNotPolluteScrollback()
    {
        var emulator = Make(2, 10);
        emulator.Feed($"{Esc}[?1049h");
        for (var index = 0; index < 10; index++) emulator.Feed($"{index}\r\n");
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    [Fact]
    public void CursorVisibilityMode()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[?25l");
        Assert.False(emulator.CursorVisible);
        emulator.Feed($"{Esc}[?25h");
        Assert.True(emulator.CursorVisible);
    }

    [Fact]
    public void TracksCursorKeyAndPasteModes()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}[?1h{Esc}[?2004h");
        Assert.True(emulator.ApplicationCursorKeys);
        Assert.True(emulator.BracketedPaste);
        emulator.Feed($"{Esc}[?1l{Esc}[?2004l");
        Assert.False(emulator.ApplicationCursorKeys);
        Assert.False(emulator.BracketedPaste);
    }

    [Fact]
    public void LineDrawingCharset()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}(0lqk{Esc}(Bx");
        Assert.Equal("\u250C\u2500\u2510x", Line(emulator, 0));
    }

    // MARK: OSC, strings and replies

    [Fact]
    public void OscTitleWithBel()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}]0;my title\u0007rest");
        Assert.Equal("my title", emulator.Title);
        Assert.Equal("rest", Line(emulator, 0));
    }

    [Fact]
    public void OscTitleWithStringTerminator()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}]2;C:\\Users\\me{Esc}\\rest");
        Assert.Equal("C:\\Users\\me", emulator.Title);
        Assert.Equal("rest", Line(emulator, 0));
    }

    [Fact]
    public void OscTitleDecodesUtf8()
    {
        var emulator = Make();
        emulator.Feed($"{Esc}]0;caf\u00E9\u0007");
        Assert.Equal("caf\u00E9", emulator.Title);
    }

    [Fact]
    public void DcsPayloadIsNotPrinted()
    {
        var emulator = Make();
        emulator.Feed($"a{Esc}P1$r0m{Esc}\\b");
        Assert.Equal("ab", Line(emulator, 0));
    }

    [Fact]
    public void DeviceStatusReportAnswersCursorPosition()
    {
        var emulator = Make();
        var replies = new List<string>();
        emulator.OnReply = data => replies.Add(Encoding.UTF8.GetString(data));
        emulator.Feed($"{Esc}[3;7H{Esc}[6n");
        Assert.Equal([$"{Esc}[3;7R"], replies);
    }

    [Fact]
    public void UnknownSequenceIsSwallowedNotPrinted()
    {
        var emulator = Make();
        emulator.Feed($"a{Esc}[?9001hb{Esc}[5 qc");
        Assert.Equal("abc", Line(emulator, 0));
    }

    [Fact]
    public void BellInvokesCallback()
    {
        var emulator = Make();
        var rang = 0;
        emulator.OnBell = () => rang++;
        emulator.Feed("\u0007");
        Assert.Equal(1, rang);
    }

    [Fact]
    public void RepeatsPreviousCharacter()
    {
        var emulator = Make();
        emulator.Feed($"-{Esc}[4b");
        Assert.Equal("-----", Line(emulator, 0));
    }

    // MARK: Resize

    [Fact]
    public void ResizeKeepsContentAndClampsCursor()
    {
        var emulator = Make(4, 20);
        emulator.Feed("hello\r\nworld");
        emulator.Resize(2, 10);
        Assert.Equal(2, emulator.Rows);
        Assert.Equal(10, emulator.Cols);
        Assert.True(emulator.CursorRow < 2);
        Assert.Contains(emulator.AllLines, l => TerminalEmulator.PlainText(l) == "hello");
    }

    [Fact]
    public void ResizeShorterPushesTopIntoScrollback()
    {
        var emulator = Make(4, 10);
        emulator.Feed("1\r\n2\r\n3\r\n4");
        emulator.Resize(2, 10);
        Assert.Equal("3", Line(emulator, 0));
        Assert.Equal("4", Line(emulator, 1));
        Assert.Equal(2, emulator.ScrollbackCount);
    }

    [Fact]
    public void ResizeToWiderPadsRows()
    {
        var emulator = Make(2, 5);
        emulator.Feed("abc");
        emulator.Resize(3, 12);
        Assert.Equal(12, emulator.Screen[0].Length);
        Assert.Equal("abc", Line(emulator, 0));
    }

    // MARK: Wide characters

    [Fact]
    public void WideCharacterOccupiesTwoCells()
    {
        var emulator = Make(2, 10);
        emulator.Feed("\u6F22\u5B57");
        Assert.Equal(4, emulator.CursorCol);
        Assert.True(emulator.Screen[0][1].IsContinuation);
        Assert.Equal("\u6F22\u5B57", Line(emulator, 0));
    }

    [Fact]
    public void WideCharacterAtMarginWrapsWhole()
    {
        var emulator = Make(3, 5);
        emulator.Feed("abcd\u6F22");
        Assert.Equal("abcd", Line(emulator, 0));
        Assert.Equal("\u6F22", Line(emulator, 1));
    }

    // MARK: Selection helpers

    [Fact]
    public void TextBetweenSpansLines()
    {
        var emulator = Make(3, 10);
        emulator.Feed("hello\r\nworld");
        Assert.Equal("llo\nwor", emulator.TextBetween((0, 2), (1, 3)));
        // Reversed endpoints read the same.
        Assert.Equal("llo\nwor", emulator.TextBetween((1, 3), (0, 2)));
    }

    [Fact]
    public void WordAtFindsPathLikeWords()
    {
        var emulator = Make(2, 30);
        emulator.Feed("open C:\\src\\app.cs now");
        var word = emulator.WordAt(0, 8);
        Assert.NotNull(word);
        Assert.Equal("C:\\src\\app.cs", TerminalEmulator.PlainText(emulator.Screen[0], word!.Value.Start, word.Value.End));
    }

    [Fact]
    public void TranscriptJoinsHistoryAndScreen()
    {
        var emulator = Make(2, 10);
        emulator.Feed("a\r\nb\r\nc");
        Assert.Equal("a\nb\nc", emulator.Transcript);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var emulator = Make(2, 10);
        emulator.Feed($"a\r\nb\r\nc{Esc}[31m{Esc}[?1h");
        emulator.Reset();
        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal("", Line(emulator, 0));
        Assert.False(emulator.ApplicationCursorKeys);
        emulator.Feed("x");
        Assert.Equal(CellStyle.Normal, emulator.Screen[0][0].Style);
    }
}
