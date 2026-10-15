using Vision.App.Infrastructure;

namespace Vision.App.Tests;

/// <summary>New: the <c>--migrate-only</c> command line. What it does is checked end to end on a real upgrade by
/// scripts/installer-test.ps1 in CI (install the former version, fill its data, install Vision, run it).</summary>
public sealed class LegacyUpgradeTests
{
    [Fact]
    public void MigrateOnlyTakesAnOptionalReportPath()
    {
        Assert.Null(LegacyUpgrade.ParseMigrateOnly([]));
        Assert.Null(LegacyUpgrade.ParseMigrateOnly([@"C:\code\project"]));
        Assert.Equal("", LegacyUpgrade.ParseMigrateOnly(["--migrate-only"]));
        Assert.Equal(@"C:\temp\report.json", LegacyUpgrade.ParseMigrateOnly(["--migrate-only", @"C:\temp\report.json"]));
        Assert.Equal(@"r.json", LegacyUpgrade.ParseMigrateOnly(["--MIGRATE-ONLY", "r.json"]));
        // A following flag isn't a report path.
        Assert.Equal("", LegacyUpgrade.ParseMigrateOnly(["--migrate-only", "--theme", "dark"]));
    }

    [Fact]
    public void TheExitCodesAreDistinct()
    {
        int[] codes =
        [
            LegacyUpgrade.ExitCodes.Done, LegacyUpgrade.ExitCodes.Failed,
            LegacyUpgrade.ExitCodes.LegacyAppRunning, LegacyUpgrade.ExitCodes.AppRunning,
        ];
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, LegacyUpgrade.ExitCodes.Done);
    }
}
