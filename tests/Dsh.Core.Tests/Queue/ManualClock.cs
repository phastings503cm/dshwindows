namespace Dsh.Core.Tests;

/// <summary>A clock that only moves when told to, in UTC, so queue timestamps, durations, rates and
/// relative times are exact.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    /// <summary>2026-09-24 10:32:05 UTC.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 9, 24, 10, 32, 5, TimeSpan.Zero);

    private readonly Lock _lock = new();
    private DateTimeOffset _now = start;

    public ManualClock() : this(Epoch) { }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by)
    {
        lock (_lock) _now += by;
    }

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
