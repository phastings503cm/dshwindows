using System.Globalization;

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

    /// <summary>"14:32" — transcript timestamps.</summary>
    public static string Clock(DateTimeOffset date) => date.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public static string Plural(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";
}
