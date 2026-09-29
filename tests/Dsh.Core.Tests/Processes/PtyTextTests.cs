namespace Dsh.Core.Tests;

/// <summary>Ported from PTYTextTests in MachineTests.swift, plus the pseudo-console shapes the Windows
/// port meets: ConPTY re-renders the console screen, so blanks arrive as cursor-forward moves and
/// repaints as absolute cursor positioning.</summary>
public sealed class PtyTextTests
{
    [Fact]
    public void StripsAnsiCodesAndControls()
    {
        var raw = "\u001B[31mRED\u001B[0m bell\u0007 done\u0008\u001B]0;title\u0007tail";
        var clean = PtyText.Clean(raw);
        Assert.Equal("RED bell donetail", clean); // OSC swallows "0;title\a"
        Assert.DoesNotContain('\u001B', clean);
    }

    [Fact]
    public void CsiWithPrivatePrefixConsumedToFinalByte()
    {
        // Cursor save/restore, clear-line: all CSI.
        Assert.Equal("keep me", PtyText.Clean("\u001B[2K\u001B[?25lkeep me\u001B[?25h"));
    }

    [Fact]
    public void SpinnerCrKeepsLastFragment()
    {
        // The raw-CR shape (curl/npm style); the cursor-positioning shape is covered below.
        var clean = PtyText.Clean("downloading\r50%\r100% done\nreal line\n");
        Assert.Contains("100% done", clean);
        Assert.DoesNotContain("downloading50%", clean);
        Assert.Contains("real line", clean);
    }

    [Fact]
    public void BlankRunCollapse()
    {
        var clean = PtyText.Clean("a\n\n\n\n\nb");
        // At most two blanks survive.
        Assert.Equal(4, clean.Split('\n').Length);
        Assert.Equal("a\n\n\nb", clean);
    }

    [Fact]
    public void CrLfLineEndingsBecomePlainLines()
    {
        Assert.Equal("one\ntwo\n", PtyText.Clean("one\r\ntwo\r\n"));
    }

    [Fact]
    public void ConPtyStartupPreambleLeavesNothing()
    {
        // What a pseudo console sends before the program prints anything: win32-input-mode and focus
        // requests, hide cursor, clear, home, the window title, show cursor.
        var preamble = "\u001B[?9001h\u001B[?1004h\u001B[?25l\u001B[2J\u001B[m\u001B[H\u001B]0;C:\\Program Files\\PowerShell\\7\\pwsh.exe\u0007\u001B[?25h";
        Assert.Equal("", PtyText.Clean(preamble));
        Assert.Equal("hello-from-bg\n", PtyText.Clean(preamble + "hello-from-bg\r\n\u001B[K"));
    }

    [Fact]
    public void CursorForwardIsDrawnAsSpaces()
    {
        // ConPTY paints a run of blanks inside a line as "erase characters" + "cursor forward".
        Assert.Equal("Name    Value", PtyText.Clean("Name\u001B[4X\u001B[4CValue"));
        Assert.Equal("a b", PtyText.Clean("a\u001B[Cb"));
    }

    [Fact]
    public void CursorPositionOnAnotherRowStartsANewLine()
    {
        // A full repaint: each row addressed absolutely.
        var clean = PtyText.Clean("\u001B[1;1Hfirst row\u001B[2;1Hsecond row\u001B[4;1Hfourth row");
        Assert.Equal("first row\nsecond row\nfourth row", clean);
    }

    [Fact]
    public void CursorPositionBackToTheSameRowOverwritesLikeACarriageReturn()
    {
        // The spinner shape through a pseudo console: each tick homes the cursor on the same row.
        var clean = PtyText.Clean("\u001B[5;1HProgress 10%\u001B[5;1HProgress 55%\u001B[5;1HProgress 100%\r\nnext");
        Assert.Equal("Progress 100%\nnext", clean);
    }

    [Fact]
    public void RowTrackingFollowsNewlines()
    {
        // After "\r\n" the cursor is on row 4; a jump back to row 3 is a different row (a new line),
        // and a jump to row 4 column 1 rewrites the current one.
        var clean = PtyText.Clean("\u001B[3;1Hline three\r\nold four\u001B[4;1Hnew four\u001B[3;1Hthree again");
        Assert.Equal("line three\nnew four\nthree again", clean);
    }

    [Fact]
    public void CursorUpRewritesGetTheirOwnLine()
    {
        // docker/npm style multi-line progress: move up, clear the line, print the update.
        var clean = PtyText.Clean("layer 1: 10%\nlayer 2: 20%\n\u001B[2A\u001B[2Klayer 1: done\n");
        Assert.Contains("layer 1: done", clean);
        Assert.DoesNotContain("20%layer", clean);
    }

    [Fact]
    public void ColumnOneMoveIsACarriageReturn()
    {
        Assert.Equal("done", PtyText.Clean("working...\u001B[1Gdone"));
        Assert.Equal("working...", PtyText.Clean("working\u001B[8G..."));
    }

    [Fact]
    public void CharsetDesignationAndStringSequencesAreSwallowed()
    {
        // "ESC ( B" (tput sgr0) must not leak the "B"; DCS and APC run to the string terminator.
        Assert.Equal("plain", PtyText.Clean("\u001B(Bpl\u001BP1$r0m\u001B\\ain\u001B_app\u001B\\"));
        Assert.Equal("ok", PtyText.Clean("\u001B7ok\u001B8\u001B=\u001B>"));
    }

    [Fact]
    public void TruncatedEscapeAtTheEndIsDropped()
    {
        Assert.Equal("text", PtyText.Clean("text\u001B[12;"));
        Assert.Equal("text", PtyText.Clean("text\u001B"));
        Assert.Equal("text", PtyText.Clean("text\u001B]0;unterminated title"));
    }

    [Fact]
    public void KeepsTabsAndUnicode()
    {
        Assert.Equal("a\tb — ünïcödé 😀", PtyText.Clean("a\tb — ünïcödé 😀"));
    }

    [Fact]
    public void BogusCursorForwardIsBounded()
    {
        Assert.True(PtyText.Clean("x\u001B[999999999Cy").Length <= 402);
    }
}
