namespace Dsh.Core.Tests.Text;

public class SyntaxScannerTests
{
    private static List<(SyntaxKind Kind, string Text)> Tokens(string text, Language language) =>
        SyntaxScanner.Scan(text, language).Select(t => (t.Kind, text.Substring(t.Start, t.Length))).ToList();

    [Fact]
    public void ColoursCSharpKeywordsStringsNumbersAndComments()
    {
        var tokens = Tokens("var x = \"hi\"; // note\nreturn 42;", Language.CSharp);
        Assert.Contains((SyntaxKind.Keyword, "var"), tokens);
        Assert.Contains((SyntaxKind.String, "\"hi\""), tokens);
        Assert.Contains((SyntaxKind.Comment, "// note"), tokens);
        Assert.Contains((SyntaxKind.Keyword, "return"), tokens);
        Assert.Contains((SyntaxKind.Number, "42"), tokens);
    }

    [Fact]
    public void BlockCommentStateCarriesAcrossLines()
    {
        var lines = new List<SyntaxToken>();
        var inside = SyntaxScanner.ScanLine("int a; /* start", Language.CSharp, false, lines);
        Assert.True(inside);
        lines.Clear();
        inside = SyntaxScanner.ScanLine("still comment", Language.CSharp, inside, lines);
        Assert.True(inside);
        Assert.Equal([new SyntaxToken(SyntaxKind.Comment, 0, 13)], lines);
        lines.Clear();
        inside = SyntaxScanner.ScanLine("end */ return", Language.CSharp, inside, lines);
        Assert.False(inside);
        Assert.Equal(new SyntaxToken(SyntaxKind.Comment, 0, 6), lines[0]);
        Assert.Equal(new SyntaxToken(SyntaxKind.Keyword, 7, 6), lines[1]);
    }

    [Fact]
    public void KeywordsInsideStringsAreNotKeywords()
    {
        var tokens = Tokens("\"return if\"", Language.CSharp);
        Assert.Equal([(SyntaxKind.String, "\"return if\"")], tokens);
    }

    [Fact]
    public void UnterminatedStringEndsAtLineBreak()
    {
        var tokens = Tokens("\"open\nreturn", Language.CSharp);
        Assert.Contains((SyntaxKind.String, "\"open"), tokens);
        Assert.Contains((SyntaxKind.Keyword, "return"), tokens);
    }

    [Fact]
    public void PowerShellIsCaseInsensitiveWithVariablesAndOperators()
    {
        var tokens = Tokens("If ($true -EQ $x) { Write-Host 'a' } # done", Language.PowerShell);
        Assert.Contains((SyntaxKind.Keyword, "If"), tokens);
        Assert.Contains((SyntaxKind.Keyword, "$true"), tokens);
        Assert.Contains((SyntaxKind.Keyword, "-EQ"), tokens);
        Assert.DoesNotContain(tokens, t => t.Text == "$x");
        Assert.Contains((SyntaxKind.String, "'a'"), tokens);
        Assert.Contains((SyntaxKind.Comment, "# done"), tokens);
    }

    [Fact]
    public void PowerShellBlockComments()
    {
        var tokens = Tokens("<# help\n text #> Get-Item", Language.PowerShell);
        Assert.Equal((SyntaxKind.Comment, "<# help"), tokens[0]);
        Assert.Equal((SyntaxKind.Comment, " text #>"), tokens[1]);
    }

    [Fact]
    public void MarkupColoursTagNamesAndComments()
    {
        var tokens = Tokens("<Grid Margin=\"4\"><!-- note --></Grid>", Language.Markup);
        Assert.Contains((SyntaxKind.Tag, "Grid"), tokens);
        Assert.Contains((SyntaxKind.String, "\"4\""), tokens);
        Assert.Contains((SyntaxKind.Comment, "<!-- note -->"), tokens);
        Assert.Equal(2, tokens.Count(t => t == (SyntaxKind.Tag, "Grid")));
    }

    [Fact]
    public void BatchCommentsAndKeywords()
    {
        var tokens = Tokens(":: build\r\nECHO off", Language.Batch);
        Assert.Contains((SyntaxKind.Comment, ":: build\r"), tokens);
        Assert.Contains((SyntaxKind.Keyword, "ECHO"), tokens);
    }

    [Fact]
    public void PlainTextHasNoTokens() => Assert.Empty(Tokens("if return 42 \"x\"", Language.Plain));
}
