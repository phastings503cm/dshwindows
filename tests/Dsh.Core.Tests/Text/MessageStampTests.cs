using System.Globalization;

namespace Dsh.Core.Tests;

/// <summary>The date and time shown on every chat message: this PC's clock, the user's own formats.</summary>
public sealed class MessageStampTests
{
    // Pacific daylight time; 18:42:05 UTC is 11:42:05 there, on Friday 2 October 2026.
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.CreateCustomTimeZone("test-pdt", TimeSpan.FromHours(-7), "PDT", "PDT");
    private static readonly DateTimeOffset Sent = new(2026, 10, 2, 18, 42, 5, TimeSpan.Zero);

    // ICU puts a narrow no-break space before AM/PM; what matters here is the content, not which space.
    private static string Plain(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

    [Fact]
    public void ShowsTheDateAndTimeOnTheLocalClock()
    {
        Assert.Equal("Fri, Oct 2, 2026 · 11:42:05 AM", Plain(Fmt.Stamp(Sent, new CultureInfo("en-US"), Pacific)));
        // The same instant stored with another offset reads the same.
        Assert.Equal("Fri, Oct 2, 2026 · 11:42:05 AM", Plain(Fmt.Stamp(Sent.ToOffset(TimeSpan.FromHours(9)), new CultureInfo("en-US"), Pacific)));
    }

    [Fact]
    public void CrossingMidnightChangesTheDate()
    {
        var late = new DateTimeOffset(2026, 10, 3, 6, 59, 0, TimeSpan.Zero); // 23:59 the evening before, in Pacific time
        Assert.Equal("Fri, Oct 2, 2026 · 11:59:00 PM", Plain(Fmt.Stamp(late, new CultureInfo("en-US"), Pacific)));
        Assert.Equal("Sat, Oct 3, 2026 · 12:00:00 AM", Plain(Fmt.Stamp(late.AddMinutes(1), new CultureInfo("en-US"), Pacific)));
    }

    [Fact]
    public void DayFirstCulturesPutTheDayFirst()
    {
        Assert.Equal("Fri 2 Oct 2026 · 11:42:05", Plain(Fmt.Stamp(Sent, new CultureInfo("en-GB"), Pacific)));
        var german = Plain(Fmt.Stamp(Sent, new CultureInfo("de-DE"), Pacific));
        Assert.StartsWith("Fr", german);
        Assert.Contains("2. Okt", german);
        Assert.EndsWith("2026 · 11:42:05", german);
    }

    [Fact]
    public void TheFullStampHasTheOffset()
    {
        var full = Plain(Fmt.FullStamp(Sent, new CultureInfo("en-US"), Pacific));
        Assert.StartsWith("Friday, October 2, 2026", full);
        Assert.Contains("11:42:05 AM", full);
        Assert.EndsWith("(UTC-07:00)", full);
    }
}
