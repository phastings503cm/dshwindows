namespace Dsh.Core.Tests;

/// <summary>New: inline markdown runs (the Swift app delegated this to AttributedString).</summary>
public sealed class MarkdownInlineTests
{
    private static InlineRun Run(string text, InlineStyle style = InlineStyle.None, string? url = null) => new(text, style, url);

    private static void AssertRuns(string markdown, params InlineRun[] expected) =>
        Assert.Equal(expected, MarkdownInline.Parse(markdown));

    [Fact]
    public void PlainTextIsOneRun() => AssertRuns("just text", Run("just text"));

    [Fact]
    public void Bold() => AssertRuns("a **b** c", Run("a "), Run("b", InlineStyle.Bold), Run(" c"));

    [Fact]
    public void ItalicWithAsterisksAndUnderscores()
    {
        AssertRuns("*it*", Run("it", InlineStyle.Italic));
        AssertRuns("an _it_ here", Run("an "), Run("it", InlineStyle.Italic), Run(" here"));
    }

    [Fact]
    public void BoldItalic()
    {
        AssertRuns("***both***", Run("both", InlineStyle.Bold | InlineStyle.Italic));
        AssertRuns("__strong__", Run("strong", InlineStyle.Bold));
    }

    [Fact]
    public void CodeSpansAreLiteralAndStarsInsideDoNotStartEmphasis()
    {
        AssertRuns("run `a*b*c` now", Run("run "), Run("a*b*c", InlineStyle.Code), Run(" now"));
        AssertRuns("`*` then *x*", Run("*", InlineStyle.Code), Run(" then "), Run("x", InlineStyle.Italic));
        // A code span inside emphasis doesn't close it early.
        AssertRuns("*a `*` b*", Run("a ", InlineStyle.Italic), Run("*", InlineStyle.Italic | InlineStyle.Code), Run(" b", InlineStyle.Italic));
    }

    [Fact]
    public void CodeSpanDelimitersAndPadding()
    {
        AssertRuns("``a`b``", Run("a`b", InlineStyle.Code));
        AssertRuns("` code `", Run("code", InlineStyle.Code));
    }

    [Fact]
    public void Strikethrough() => AssertRuns("~~gone~~ kept", Run("gone", InlineStyle.Strike), Run(" kept"));

    [Fact]
    public void Links()
    {
        AssertRuns("see [the docs](https://example.com/docs) now",
            Run("see "), Run("the docs", InlineStyle.Link, "https://example.com/docs"), Run(" now"));
        // An optional title is dropped; formatting inside the label is kept.
        AssertRuns("[**b**](https://x.io \"title\")", Run("b", InlineStyle.Bold | InlineStyle.Link, "https://x.io"));
    }

    [Fact]
    public void AutolinksAndBareUrls()
    {
        AssertRuns("<https://example.com/a>", Run("https://example.com/a", InlineStyle.Link, "https://example.com/a"));
        AssertRuns("go to https://example.com/x.",
            Run("go to "), Run("https://example.com/x", InlineStyle.Link, "https://example.com/x"), Run("."));
        AssertRuns("(see https://x.io/a_b_c)",
            Run("(see "), Run("https://x.io/a_b_c", InlineStyle.Link, "https://x.io/a_b_c"), Run(")"));
        // Not a URL when glued to a word.
        AssertRuns("xhttps://nope", Run("xhttps://nope"));
    }

    [Fact]
    public void BackslashEscapes()
    {
        AssertRuns(@"\*not italic\*", Run("*not italic*"));
        AssertRuns(@"a \_b\_ c", Run("a _b_ c"));
        // Windows paths keep their backslashes: letters are not escapable.
        AssertRuns(@"C:\Users\me", Run(@"C:\Users\me"));
    }

    [Fact]
    public void IntrawordUnderscoresStayPlain()
    {
        AssertRuns("call snake_case_name here", Run("call snake_case_name here"));
        AssertRuns("_snake_case_", Run("snake_case", InlineStyle.Italic));
    }

    [Fact]
    public void UnmatchedDelimitersStayLiteral()
    {
        AssertRuns("**unclosed", Run("**unclosed"));
        AssertRuns("a * b", Run("a * b"));
        AssertRuns("2 * 3 * 4", Run("2 * 3 * 4"));
        AssertRuns("trailing*", Run("trailing*"));
        AssertRuns("`unclosed code", Run("`unclosed code"));
        AssertRuns("~~x", Run("~~x"));
        AssertRuns("[not a link] (x)", Run("[not a link] (x)"));
    }

    [Fact]
    public void NestedEmphasis()
    {
        AssertRuns("*a **b** c*",
            Run("a ", InlineStyle.Italic), Run("b", InlineStyle.Bold | InlineStyle.Italic), Run(" c", InlineStyle.Italic));
        AssertRuns("**a *b* c**",
            Run("a ", InlineStyle.Bold), Run("b", InlineStyle.Bold | InlineStyle.Italic), Run(" c", InlineStyle.Bold));
    }

    [Fact]
    public void AdjacentRunsWithTheSameStyleMerge()
    {
        // The escape and the text around it end up in one run.
        AssertRuns(@"a\*b", Run("a*b"));
        Assert.Equal("bold and link", MarkdownInline.Plain("**bold** and [link](https://x.io)"));
    }
}
