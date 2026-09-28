namespace Dsh.Core.Tests;

/// <summary>New: string helpers (surrogate-safe cuts, console output cleanup, counting).</summary>
public sealed class TextUtilTests
{
    private const string Emoji = "\U0001F600"; // one code point, two UTF-16 units

    [Fact]
    public void PrefixNeverSplitsASurrogatePair()
    {
        var s = "a" + Emoji + "b"; // a, high, low, b
        Assert.Equal("a", TextUtil.Prefix(s, 2));
        Assert.Equal("a" + Emoji, TextUtil.Prefix(s, 3));
        Assert.Equal(s, TextUtil.Prefix(s, 4));
        Assert.Equal(s, TextUtil.Prefix(s, 100));
        Assert.Equal("", TextUtil.Prefix(s, 0));
        Assert.Equal("", TextUtil.Prefix(s, -1));
        Assert.Equal("", TextUtil.Prefix(Emoji, 1));
    }

    [Fact]
    public void SuffixNeverSplitsASurrogatePair()
    {
        var s = "a" + Emoji + "b";
        Assert.Equal("b", TextUtil.Suffix(s, 2));
        Assert.Equal(Emoji + "b", TextUtil.Suffix(s, 3));
        Assert.Equal(s, TextUtil.Suffix(s, 4));
        Assert.Equal("", TextUtil.Suffix(s, 0));
        Assert.Equal("", TextUtil.Suffix(Emoji, 1));
    }

    [Fact]
    public void CutsNeverLeaveALoneSurrogate()
    {
        var s = string.Concat(Enumerable.Repeat("x" + Emoji, 50));
        for (var n = 0; n <= s.Length; n++)
        {
            Assert.False(HasLoneSurrogate(TextUtil.Prefix(s, n)), $"prefix {n}");
            Assert.False(HasLoneSurrogate(TextUtil.Suffix(s, n)), $"suffix {n}");
        }
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void CleanConsoleOutputStripsAnsiSequences()
    {
        Assert.Equal("red plain", TextUtil.CleanConsoleOutput("\u001b[31mred\u001b[0m plain"));
        Assert.Equal("bold", TextUtil.CleanConsoleOutput("\u001b[1;38;5;208mbold\u001b[m"));
        Assert.Equal("text", TextUtil.CleanConsoleOutput("\u001b]0;window title\u0007text"));
        Assert.Equal("link", TextUtil.CleanConsoleOutput("\u001b]8;;https://x.io\u001b\\link\u001b]8;;\u001b\\"));
        Assert.Equal("ab", TextUtil.CleanConsoleOutput("a\u001b[2Kb"));
        Assert.Equal("no escapes", TextUtil.StripAnsi("no escapes"));
    }

    [Fact]
    public void CleanConsoleOutputAppliesCarriageReturnOverwrites()
    {
        Assert.Equal("done", TextUtil.CleanConsoleOutput("10%\r50%\rdone"));
        Assert.Equal(" 50%", TextUtil.CleanConsoleOutput("100%\r 50%"));
        // A shorter overwrite keeps the rest of the earlier text, as a terminal would show it.
        Assert.Equal("donealling...", TextUtil.CleanConsoleOutput("installing...\rdone"));
        Assert.Equal("a\nb\n", TextUtil.CleanConsoleOutput("a\r\nb\r\n")); // CRLF is just a line break
        Assert.Equal("step 2\nnext", TextUtil.CleanConsoleOutput("step 1\rstep 2\r\nnext"));
    }

    [Fact]
    public void CountOccurrencesIsOrdinalAndNonOverlapping()
    {
        Assert.Equal(2, TextUtil.CountOccurrences("aaaa", "aa"));
        Assert.Equal(2, TextUtil.CountOccurrences("a.b.c", "."));
        Assert.Equal(0, TextUtil.CountOccurrences("abc", ""));
        Assert.Equal(0, TextUtil.CountOccurrences("", "a"));
        Assert.Equal(0, TextUtil.CountOccurrences("ABC", "abc"));
        Assert.Equal(1, TextUtil.CountOccurrences("line\r\nline", "\r\n"));
    }

    [Fact]
    public void NewlinesLinesAndFirstLine()
    {
        Assert.Equal("a\nb\nc\n", TextUtil.NormalizeNewlines("a\r\nb\rc\n"));
        string[] lines = ["a", "", "b", ""];
        Assert.Equal(lines, TextUtil.Lines("a\n\nb\n"));
        Assert.Equal("first", TextUtil.FirstLine("\n\nfirst\nsecond"));
        Assert.Equal("", TextUtil.FirstLine(""));
    }

    [Fact]
    public void EncodingsBehaveAsDocumented()
    {
        Assert.Empty(TextUtil.Utf8NoBom.GetPreamble());
        Assert.Throws<System.Text.DecoderFallbackException>(() => TextUtil.Utf8Strict.GetString([0xC3, 0x28]));
    }
}

/// <summary>New: lenient tool-argument readers and integral-number parsing.</summary>
public sealed class JsonArgsTests
{
    [Fact]
    public void BoolsAcceptStringsAndFallBack()
    {
        Assert.True(JsonArgs.Bool("""{"x":true}""", "x", false));
        Assert.True(JsonArgs.Bool("""{"x":"true"}""", "x", false));
        Assert.True(JsonArgs.Bool("""{"x":" YES "}""", "x", false));
        Assert.True(JsonArgs.Bool("""{"x":"1"}""", "x", false));
        Assert.False(JsonArgs.Bool("""{"x":"false"}""", "x", true));
        Assert.False(JsonArgs.Bool("""{"x":"no"}""", "x", true));
        Assert.True(JsonArgs.Bool("""{"x":"maybe"}""", "x", true));
        Assert.True(JsonArgs.Bool("""{"x":1}""", "x", true));   // numbers aren't bools
        Assert.False(JsonArgs.Bool("{}", "x", false));
        Assert.False(JsonArgs.Bool("""{"x":null}""", "x", false));
    }

    [Fact]
    public void IntsAcceptStringsAndIntegralNumbers()
    {
        Assert.Equal(10, JsonArgs.Int("""{"n":10}""", "n", 0));
        Assert.Equal(10, JsonArgs.Int("""{"n":"10"}""", "n", 0));
        Assert.Equal(10, JsonArgs.Int("""{"n":" 10 "}""", "n", 0));
        Assert.Equal(-3, JsonArgs.Int("""{"n":"-3"}""", "n", 0));
        Assert.Equal(10, JsonArgs.Int("""{"n":10.0}""", "n", 0));
        Assert.Equal(7, JsonArgs.Int("""{"n":1.5}""", "n", 7));
        Assert.Equal(7, JsonArgs.Int("""{"n":"ten"}""", "n", 7));
        Assert.Equal(7, JsonArgs.Int("""{"n":[1]}""", "n", 7));
        Assert.Equal(7, JsonArgs.Int("{}", "n", 7));
    }

    [Fact]
    public void StringsAreOnlyStrings()
    {
        Assert.Equal("a", JsonArgs.String("""{"s":"a"}""", "s"));
        Assert.Null(JsonArgs.String("""{"s":1}""", "s"));
        Assert.Null(JsonArgs.String("""{"s":null}""", "s"));
        Assert.Null(JsonArgs.String("""{"s":{"t":"u"}}""", "s"));
        Assert.Null(JsonArgs.String("{}", "s"));
    }

    [Fact]
    public void ObjectToleratesGarbage()
    {
        Assert.Empty(JsonArgs.Object("not json"));
        Assert.Empty(JsonArgs.Object("[1,2]"));
        Assert.Empty(JsonArgs.Object(""));
        Assert.Single(JsonArgs.Object("""{"a":1}"""));
    }

    [Fact]
    public void QuoteProducesAJsonStringLiteral()
    {
        Assert.Equal("\"C:\\\\x\\\\\\u0022y\\u0022\"", JsonArgs.Quote("C:\\x\\\"y\""));
        Assert.Equal("C:\\x\\\"y\"", JsonArgs.String("{\"v\":" + JsonArgs.Quote("C:\\x\\\"y\"") + "}", "v"));
    }

    [Fact]
    public void TryGetIntTakesIntegralNumbersOnly()
    {
        Assert.True(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonNode.Parse("42"), out var parsed));
        Assert.Equal(42, parsed);
        Assert.True(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonNode.Parse("1.0"), out var integralDouble));
        Assert.Equal(1, integralDouble);
        Assert.True(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonValue.Create(3.0), out var fromDouble));
        Assert.Equal(3, fromDouble);
        Assert.True(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonValue.Create(5L), out var fromLong));
        Assert.Equal(5, fromLong);

        Assert.False(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonValue.Create(1.5), out _));
        Assert.False(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonNode.Parse("1.5"), out _));
        Assert.False(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonValue.Create("3"), out _));
        Assert.False(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonNode.Parse("1e10"), out _)); // out of range
        Assert.False(JsonNumbers.TryGetInt(System.Text.Json.Nodes.JsonNode.Parse("true"), out _));
        Assert.False(JsonNumbers.TryGetInt(null, out _));
    }

    [Fact]
    public void PositiveIgnoresZeroNegativeAndMissing()
    {
        var obj = System.Text.Json.Nodes.JsonNode.Parse("""{"a":5,"b":0,"c":-1,"d":"7"}""")!.AsObject();
        Assert.Equal(5, JsonNumbers.Positive(obj, "a"));
        Assert.Null(JsonNumbers.Positive(obj, "b"));
        Assert.Null(JsonNumbers.Positive(obj, "c"));
        Assert.Null(JsonNumbers.Positive(obj, "d"));
        Assert.Null(JsonNumbers.Positive(obj, "missing"));
    }
}
