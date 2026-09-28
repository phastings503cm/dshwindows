namespace Dsh.Core.Tests;

/// <summary>Ported from the GlobTests class in HarnessTests.swift.</summary>
public sealed class GlobTests
{
    [Fact]
    public void StarDoesNotCrossDirectories()
    {
        Assert.True(Glob.Matches("*.swift", "main.swift"));
        Assert.False(Glob.Matches("*.swift", "src/main.swift"));
    }

    [Fact]
    public void DoubleStarCrossesDirectories()
    {
        Assert.True(Glob.Matches("**/*.swift", "a/b/c.swift"));
        Assert.True(Glob.Matches("**/*.swift", "c.swift"));
    }

    [Fact]
    public void QuestionMarkMatchesOneCharacter()
    {
        Assert.True(Glob.Matches("?.txt", "a.txt"));
        Assert.False(Glob.Matches("?.txt", "ab.txt"));
    }

    [Fact]
    public void PrefixedPattern()
    {
        Assert.True(Glob.Matches("Sources/**/*.swift", "Sources/DSHCore/Engine.swift"));
        Assert.False(Glob.Matches("Sources/**/*.swift", "Tests/DSHCoreTests/X.swift"));
    }
}

/// <summary>New: Windows separators and case rules, and regex metacharacters.</summary>
public sealed class GlobWindowsTests
{
    [Fact]
    public void BackslashesInPatternsAndPathsAreSeparators()
    {
        Assert.True(Glob.Matches(@"src\*.cs", "src/a.cs"));
        Assert.True(Glob.Matches("src/*.cs", @"src\a.cs"));
        Assert.True(Glob.Matches(@"src\**\*.cs", @"src\a\b\c.cs"));
    }

    [Fact]
    public void RegexMetacharactersAreLiteral()
    {
        Assert.True(Glob.Matches("a+b(1).txt", "a+b(1).txt"));
        Assert.False(Glob.Matches("a.txt", "abtxt"));
        Assert.True(Glob.Matches("[x].md", "[x].md"));
    }

    [Fact]
    public void FileNameOnlyMatchesTheLastComponent()
    {
        Assert.True(Glob.Matches("*.cs", "src/deep/a.cs", fileNameOnly: true));
        Assert.False(Glob.Matches("*.cs", "src/deep/a.cs"));
    }

    [WindowsFact]
    public void MatchingIgnoresCaseOnWindows()
    {
        Assert.True(Glob.Matches("*.CS", "Program.cs"));
        Assert.True(Glob.Matches("SRC/**/*.cs", "src/a/b.cs"));
    }

    [UnixFact]
    public void MatchingIsCaseSensitiveOnUnix()
    {
        Assert.False(Glob.Matches("*.CS", "Program.cs"));
    }
}
