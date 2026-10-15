using Vision.App.Infrastructure;

namespace Vision.App.Tests;

/// <summary>What the update banner makes of GitHub's "latest release" answer: only the tag decides, so releases
/// titled under the app's old name, and the first ones under its new name, are both understood.</summary>
public sealed class UpdateCheckerTests
{
    private static string Latest(string tag, string title, string asset) =>
        $$"""{"tag_name":"{{tag}}","name":"{{title}}","html_url":"https://github.com/{{UpdateChecker.Repository}}/releases/tag/{{tag}}","assets":[{"name":"{{asset}}"}]}""";

    [Fact]
    public void TheFirstVisionReleaseIsOfferedToTheLastBuildUnderTheOldName()
    {
        var release = UpdateChecker.NewerThan(Latest("v0.9.90", "Vision 0.9.90", "Vision-0.9.90-win-x64-setup.exe"), new Version(0, 8, 85));

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 9, 90), release.Version);
        Assert.Equal("v0.9.90", release.Tag);
        Assert.EndsWith("/releases/tag/v0.9.90", release.Url);
    }

    [Fact]
    public void AReleaseTitledUnderTheOldNameIsReadByItsTag()
    {
        // Legacy: releases before 0.9 were titled and named after DSH, the app's former name.
        Assert.NotNull(UpdateChecker.NewerThan(Latest("v0.8.85", "DSH 0.8.85", "DSH-0.8.85-win-x64-setup.exe"), new Version(0, 8, 80)));
        Assert.Null(UpdateChecker.NewerThan(Latest("v0.8.85", "DSH 0.8.85", "DSH-0.8.85-win-x64-setup.exe"), new Version(0, 9, 90)));
    }

    [Theory]
    [InlineData("v0.9.90", "0.9.90")]   // the same build
    [InlineData("v0.9.89", "0.9.90")]   // older
    [InlineData("nightly", "0.9.90")]   // not a version
    public void NothingIsOfferedUnlessTheTagIsNewer(string tag, string current) =>
        Assert.Null(UpdateChecker.NewerThan(Latest(tag, $"Vision {tag}", "x"), Version.Parse(current)));

    [Fact]
    public void ABrokenAnswerOffersNothing() =>
        Assert.Null(UpdateChecker.NewerThan("<html>rate limited</html>", new Version(0, 9, 0)));

    [Fact]
    public void ReleasesComeFromTheVisionRepository() =>
        Assert.Equal("phastings503cm/vision-windows", UpdateChecker.Repository);
}
