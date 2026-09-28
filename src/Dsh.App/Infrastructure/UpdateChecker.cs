using System.Net.Http;
using System.Text.Json.Nodes;

namespace Dsh.App.Infrastructure;

/// <summary>Every push to the default branch publishes a release; this tells the user when a newer
/// one than the running build is out. It only reads the public releases API — nothing is downloaded
/// or installed without the user clicking through.</summary>
public static class UpdateChecker
{
    public const string Repository = "phastings503cm/dshwindows";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    public sealed record Release(Version Version, string Tag, string Url);

    public static Version Current
    {
        get
        {
            var text = Dsh.Core.AppInfo.Version;
            return Version.TryParse(text.Split('-')[0], out var v) ? v : new Version(0, 0, 0);
        }
    }

    /// <summary>The latest release when it is newer than this build, else null (also on any failure).</summary>
    public static async Task<Release?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DSH-Windows/{Dsh.Core.AppInfo.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
            if (JsonNode.Parse(json) is not JsonObject obj) return null;
            var tag = obj["tag_name"]?.GetValue<string>() ?? "";
            var url = obj["html_url"]?.GetValue<string>() ?? ReleasesPage;
            if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest)) return null;
            return Normalize(latest) > Normalize(Current) ? new Release(latest, tag, url) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
