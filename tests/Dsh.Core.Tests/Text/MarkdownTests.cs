namespace Dsh.Core.Tests;

/// <summary>Ported from the MarkdownTests class in HarnessTests.swift.</summary>
public sealed class MarkdownTests
{
    [Fact]
    public void HeadingsAndParagraphs()
    {
        var blocks = Markdown.Parse("# Title\n\nSome text.\n");
        Assert.Equal(new MarkdownBlock.Heading(1, "Title"), blocks[0]);
        Assert.Equal(new MarkdownBlock.Paragraph("Some text."), blocks[1]);
    }

    [Fact]
    public void FencedCodeKeepsLanguageAndBody()
    {
        Assert.Equal(new MarkdownBlock.Code("swift", "let x = 1"), Markdown.Parse("```swift\nlet x = 1\n```")[0]);
    }

    /// <summary>A response still streaming has an open fence; it should render as code rather than as
    /// literal backticks.</summary>
    [Fact]
    public void UnterminatedFenceClosesAtEndOfInput()
    {
        var code = Assert.IsType<MarkdownBlock.Code>(Markdown.Parse("```\npartial output")[0]);
        Assert.Equal("partial output", code.Text);
        Assert.Null(code.Language);
    }

    [Fact]
    public void BulletsOrdinalsAndTaskItems()
    {
        var markers = Markdown.Parse("- one\n2. two\n- [ ] todo\n- [x] done")
            .OfType<MarkdownBlock.ListItem>().Select(i => i.Marker);
        Assert.Equal(["•", "2.", "☐", "☑"], markers);
    }

    [Fact]
    public void NestedListDepth()
    {
        var item = Assert.IsType<MarkdownBlock.ListItem>(Markdown.Parse("- top\n  - nested")[1]);
        Assert.Equal(1, item.Depth);
    }

    [Fact]
    public void PipeTable()
    {
        const string source = """
            | Name | Value |
            |------|-------|
            | a    | 1     |
            | b    | 2     |
            """;
        var table = Assert.IsType<MarkdownBlock.Table>(Markdown.Parse(source)[0]);
        Assert.Equal(["Name", "Value"], table.Header);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["b", "2"], table.Rows[1]);
    }

    [Fact]
    public void PipesWithoutDelimiterStayProse()
    {
        Assert.IsNotType<MarkdownBlock.Table>(Markdown.Parse("| this is not | a table")[0]);
    }

    [Fact]
    public void QuotesAndRules()
    {
        var blocks = Markdown.Parse("> quoted\n\n---");
        Assert.Equal(new MarkdownBlock.Quote("quoted"), blocks[0]);
        Assert.IsType<MarkdownBlock.Rule>(blocks[1]);
    }
}

/// <summary>New: CRLF input (Windows editors and tools) parses exactly like LF input.</summary>
public sealed class MarkdownLineEndingTests
{
    [Fact]
    public void CrlfInputParsesLikeLf()
    {
        const string lf = "# Title\n\nSome text\nwrapped.\n\n```cs\nvar x = 1;\nvar y = 2;\n```\n\n- one\n  - two\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n> quote\n";
        var crlf = lf.Replace("\n", "\r\n");

        var blocks = Markdown.Parse(crlf);
        Assert.Equal(Markdown.Parse(lf).Select(Describe), blocks.Select(Describe));
        Assert.Equal(new MarkdownBlock.Paragraph("Some text\nwrapped."), blocks[1]);
        Assert.Equal(new MarkdownBlock.Code("cs", "var x = 1;\nvar y = 2;"), blocks[2]);
        Assert.DoesNotContain(blocks.Select(Describe), d => d.Contains('\r'));
    }

    [Fact]
    public void LoneCarriageReturnsAreLineBreaksToo()
    {
        Assert.Equal(new MarkdownBlock.Heading(2, "Old Mac file"), Markdown.Parse("## Old Mac file\rbody")[0]);
    }

    /// <summary>Records holding lists compare by reference, so compare a rendering instead.</summary>
    private static string Describe(MarkdownBlock block) => block switch
    {
        MarkdownBlock.Table t => $"Table[{string.Join("|", t.Header)}][{string.Join(";", t.Rows.Select(r => string.Join("|", r)))}]",
        _ => block.ToString(),
    };
}
