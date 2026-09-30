namespace Dsh.Core.Tests;

public sealed class ClaudeProjectNamesTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-names");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void EveryCharacterButLettersAndDigitsBecomesADash()
    {
        // Windows: the drive colon and the backslash both become dashes.
        Assert.Equal("C--Users-Pat-my-app-v2", ClaudeProjectNames.EncodeName(@"C:\Users\Pat\my_app.v2"));
        // Unix: a dot-folder leaves a doubled dash, and the leading slash is a dash.
        Assert.Equal("-Users-pat--config-app", ClaudeProjectNames.EncodeName("/Users/pat/.config/app"));
        Assert.Equal("caf-", ClaudeProjectNames.EncodeName("café")); // non-ASCII letters too
        Assert.Equal("a--b", ClaudeProjectNames.EncodeName("a\U0001F600b")); // one dash per UTF-16 unit, like Claude Code's regex
    }

    [Fact]
    public void EncodingIgnoresATrailingSeparator()
    {
        var folder = Directory.CreateDirectory(_root["proj"]).FullName;
        Assert.Equal(ClaudeProjectNames.Encode(folder), ClaudeProjectNames.Encode(folder + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AFolderIsFoundFromItsOwnEncodedName()
    {
        var folder = Directory.CreateDirectory(_root["work/my_app.v2/sub-dir"]).FullName;
        var found = ClaudeProjectNames.FindFolder(ClaudeProjectNames.Encode(folder));
        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(found!), ignoreCase: true);
    }

    [Fact]
    public void AmbiguousNamesResolveToAFolderThatReallyExists()
    {
        // "a-b" the folder and "a/b" the path encode identically; either answer is a real folder.
        var hyphen = Directory.CreateDirectory(_root["x/a-b"]).FullName;
        var nested = Directory.CreateDirectory(_root["x/a/b"]).FullName;
        var encoded = ClaudeProjectNames.Encode(hyphen);
        Assert.Equal(encoded, ClaudeProjectNames.Encode(nested));

        var found = ClaudeProjectNames.FindFolder(encoded);
        Assert.NotNull(found);
        Assert.True(Directory.Exists(found));
        Assert.Equal(encoded, ClaudeProjectNames.Encode(found!));
    }

    [Fact]
    public void AFolderThatIsGoneIsNotFound()
    {
        var gone = Path.Combine(_root.Path, "moved-away", "project");
        Assert.Null(ClaudeProjectNames.FindFolder(ClaudeProjectNames.Encode(gone)));
        Assert.Null(ClaudeProjectNames.FindFolder("not-an-encoded-path"));
        Assert.Null(ClaudeProjectNames.FindFolder(""));
    }

    [Fact]
    public void TheSearchGivesUpInsteadOfWalkingTheWholeDisk()
    {
        Directory.CreateDirectory(_root["deep/one/two/three"]);
        var encoded = ClaudeProjectNames.Encode(_root["deep/one/two/three"]);
        Assert.Null(ClaudeProjectNames.FindFolder(encoded, budget: 1));
    }

    [Theory]
    [InlineData("-Users-pat-code-app", "code-app")]
    [InlineData("-home-pat-work-api", "work-api")]
    [InlineData("C--Users-Pat-code-app", "code-app")]
    [InlineData("-tmp-scratch", "tmp-scratch")]
    public void ReadableNamesDropTheHomeFolderPrefix(string encoded, string expected) =>
        Assert.Equal(expected, ClaudeProjectNames.Readable(encoded));
}
