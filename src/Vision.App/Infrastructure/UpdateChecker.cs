using System.Net.Http;
using System.Text.Json.Nodes;

namespace Vision.App.Infrastructure;

/// <summary>Every push to the default branch publishes a release; this tells the user when a newer
/// one than the running build is out. It only reads the public releases API — nothing is downloaded
/// or installed without the user clicking through.</summary>
public static class UpdateChecker
{
    /// <summary>The repository releases are published from. Builds from before the repository was renamed still ask
    /// for it under its old name, and GitHub redirects those API calls here, so they find new releases too.</summary>
    public const string Repository = "phastings503cm/vision-windows";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    public sealed record Release(Version Version, string Tag, string Url);

    public static Version Current
    {
        get
        {
            var text = Vision.Core.AppInfo.Version;
            return Version.TryParse(text.Split('-')[0], out var v) ? v : new Version(0, 0, 0);
        }
    }

    /// <summary>The latest release when it is newer than this build, else null (also on any failure).</summary>
    public static async Task<Release?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Vision-Windows/{Vision.Core.AppInfo.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
            return NewerThan(json, Current);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The release in <paramref name="json"/> (GitHub's "latest release" answer) when it is newer than
    /// <paramref name="current"/>, else null. Only the tag ("v0.9.90") counts: release titles and file names changed
    /// when the app was renamed in 0.9, and may again; the tag format never has.</summary>
    internal static Release? NewerThan(string json, Version current)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return null;
            var tag = obj["tag_name"]?.GetValue<string>() ?? "";
            var url = obj["html_url"]?.GetValue<string>() ?? ReleasesPage;
            if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest)) return null;
            return Normalize(latest) > Normalize(current) ? new Release(latest, tag, url) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
