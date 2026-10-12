using System.Globalization;
using Dsh.Core;

namespace Dsh.App.Infrastructure;

public static class Formatting
{
    /// <summary>"just now", "5m ago", "3h ago", "yesterday", "12 Sep" — used in the sidebar.</summary>
    public static string Relative(DateTimeOffset date)
    {
        var seconds = (DateTimeOffset.Now - date).TotalSeconds;
        if (seconds < 60) return "just now";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m ago";
        if (seconds < 86_400) return $"{(int)(seconds / 3600)}h ago";
        if (seconds < 172_800) return "yesterday";
        return date.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }

    /// <summary>"Fri, Oct 2, 2026 · 11:42:05 AM" — the date and time on every chat message, on this PC's clock.</summary>
    public static string Stamp(DateTimeOffset date) => Fmt.Stamp(date);

    /// <summary>"Friday, October 2, 2026 11:42:05 AM (UTC-07:00)".</summary>
    public static string FullStamp(DateTimeOffset date) => Fmt.FullStamp(date);

    public static string Plural(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";
}
