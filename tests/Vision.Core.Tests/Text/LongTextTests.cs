namespace Vision.Core.Tests;

/// <summary>Very long transcript messages collapse to their first lines with "Show more" (10-19 #14), and Copy gives a prompt
/// back as it was sent (10-19 #13).</summary>
public sealed class LongTextTests
{
    private static string Lines(int count, string prefix = "line") => string.Join("\n", Enumerable.Range(1, count).Select(i => $"{prefix} {i}"));

    [Fact]
    public void ShortTextIsShownWhole()
    {
        Assert.Null(LongText.Collapse("", LongTextKind.Prompt));
        Assert.Null(LongText.Collapse(null, LongTextKind.Prompt));
        Assert.Null(LongText.Collapse("hello", LongTextKind.Prompt));
        Assert.Null(LongText.Collapse(Lines(LongText.PromptLines), LongTextKind.Prompt));
        // A final newline does not count as a line of its own.
        Assert.Null(LongText.Collapse(Lines(LongText.PromptLines) + "\n", LongTextKind.Prompt));
        Assert.Null(LongText.Collapse(Lines(LongText.ReplyLines, "r"), LongTextKind.Reply));
    }

    [Fact]
    public void ATenThousandLinePromptShowsItsFirstFortyLines()
    {
        var text = Lines(10_214);
        var cut = LongText.Collapse(text, LongTextKind.Prompt)!;
        Assert.Equal(10_214, cut.Lines);
        Assert.Equal(LongText.PromptLines, cut.ShownLines);
        Assert.Equal(10_174, cut.HiddenLines);
        Assert.Equal(Lines(40), cut.Preview);
        Assert.StartsWith(cut.Preview, text, StringComparison.Ordinal);
        Assert.Equal("Show more · 10,174 more lines", LongText.MoreLabel(cut));
        Assert.Equal("Show less", LongText.LessLabel);
    }

    [Fact]
    public void OneLineOverTheLimitCollapses()
    {
        var cut = LongText.Collapse(Lines(LongText.PromptLines + 1), LongTextKind.Prompt)!;
        Assert.Equal(1, cut.HiddenLines);
        Assert.Equal("Show more · 1 more line", LongText.MoreLabel(cut));
    }

    [Fact]
    public void FewVeryLongLinesAreCutByCharacters()
    {
        var text = new string('x', 9_000) + "\n" + new string('y', 3_000);
        var cut = LongText.Collapse(text, LongTextKind.Prompt)!;
        Assert.Equal(LongText.PromptCharacters, cut.Preview.Length);
        Assert.Equal(2, cut.Lines);
        Assert.Equal(1, cut.HiddenLines);
        var oneLine = LongText.Collapse(new string('z', 12_000), LongTextKind.Prompt)!;
        Assert.Equal(0, oneLine.HiddenLines);
        Assert.Equal("Show more · 8 KB more", LongText.MoreLabel(oneLine));
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePairOrLeavesACarriageReturn()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 3_000)); // 6,000 UTF-16 units
        var cut = LongText.Collapse(emoji, LongTextKind.Prompt)!;
        Assert.False(char.IsHighSurrogate(cut.Preview[^1]));
        var crlf = string.Join("\r\n", Enumerable.Range(1, 60).Select(i => $"row {i}"));
        Assert.EndsWith("row 40", LongText.Collapse(crlf, LongTextKind.Prompt)!.Preview);
    }

    [Fact]
    public void RepliesAndOutputHaveTheirOwnBars()
    {
        Assert.Null(LongText.Collapse(Lines(LongText.PromptLines + 1), LongTextKind.Reply));
        var reply = LongText.Collapse(Lines(LongText.ReplyLines + 1), LongTextKind.Reply)!;
        Assert.Equal(LongText.ReplyPreviewLines, reply.ShownLines);
        var output = LongText.Collapse(Lines(10_000), LongTextKind.Output)!;
        Assert.Equal(LongText.OutputPreviewLines, output.ShownLines);
        Assert.Equal(9_800, output.HiddenLines);
    }

    [Fact]
    public void ExpandedTextPastTheInlineBarGoesInTheVirtualizedBox()
    {
        Assert.True(LongText.FitsInline(Lines(500), 500));
        Assert.False(LongText.FitsInline(Lines(10_214), 10_214));
        Assert.False(LongText.FitsInline(new string('x', LongText.InlineCharacters + 1), 1));
    }

    [Fact]
    public void LinesSplitsOnBothNewlinesAndDropsTheFinalEmptyLine()
    {
        Assert.Equal(["a", "b", "c"], LongText.Lines("a\r\nb\nc\n"));
        Assert.Equal([""], LongText.Lines(""));
        Assert.Equal(["", ""], LongText.Lines("\n\n"));
        Assert.Equal(10_214, LongText.Lines(Lines(10_214)).Length);
    }

    [Fact]
    public void ScansAHugeMessageQuickly()
    {
        var text = Lines(200_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) Assert.NotNull(LongText.Collapse(text, LongTextKind.Prompt));
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"10 scans of 200,000 lines took {watch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("check the stock levels", "check the stock levels")]
    [InlineData("🎯 /goal ship the release", "/goal ship the release")]
    [InlineData("🎯 /goal (resuming) ship the release", "/goal ship the release")]
    [InlineData("📋 Task: tidy the README", "tidy the README")]
    [InlineData("📋 Task (resuming): tidy the README", "tidy the README")]
    [InlineData("/deploy staging", "/deploy staging")]
    [InlineData("  keeps its own spacing\n\nand lines  ", "  keeps its own spacing\n\nand lines  ")]
    [InlineData("a 🎯 /goal in the middle stays", "a 🎯 /goal in the middle stays")]
    public void CopyGivesAPromptBackAsItWasSent(string shown, string copied) => Assert.Equal(copied, TranscriptCopy.Prompt(shown));
}
