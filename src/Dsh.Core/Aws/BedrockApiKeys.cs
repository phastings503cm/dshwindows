using System.Globalization;
using System.Text;

namespace Dsh.Core;

// MARK: - Short-term Bedrock API keys
//
// A short-term Bedrock API key is a presigned request in disguise, minted offline from AWS credentials
// exactly as AWS's aws-bedrock-token-generator does it: SigV4-presign POST
// https://bedrock.amazonaws.com/?Action=CallWithBearerToken for service "bedrock" in the Region, drop
// the "https://", append "&Version=1", base64 the result and prefix "bedrock-api-key-". Bedrock
// verifies the embedded signature, so the key lives as long as the presigned URL (at most 12 hours)
// and no longer than the credentials that signed it.

public static class BedrockApiKeys
{
    public const string Prefix = "bedrock-api-key-";

    /// <summary>The longest life AWS gives a short-term key.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    private static readonly Uri Endpoint = new("https://bedrock.amazonaws.com/?Action=CallWithBearerToken");

    /// <summary>Mint a key for <paramref name="region"/> valid for <paramref name="lifetime"/> (default and
    /// cap: 12 hours), cut short to when <paramref name="credentials"/> expire — a key signed by expired
    /// temporary credentials is refused anyway, and <see cref="Describe"/> should tell the truth.</summary>
    public static string Create(AwsCredentials credentials, string region, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        var life = lifetime ?? MaxLifetime;
        if (life > MaxLifetime) life = MaxLifetime;
        if (credentials.Expiration is { } expiry && expiry - now < life) life = expiry - now;
        var seconds = (long)Math.Floor(life.TotalSeconds);
        if (seconds < 1) throw new InvalidOperationException("The AWS credentials have expired; sign in again to create a Bedrock API key.");
        var url = SigV4.Presign(HttpMethod.Post, Endpoint, credentials, region, "bedrock", now, TimeSpan.FromSeconds(seconds));
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(url["https://".Length..] + "&Version=1"));
    }

    /// <summary>What a short-term key says about itself.</summary>
    public sealed record KeyInfo(string Region, DateTimeOffset Issued, DateTimeOffset Expires, string AccessKeyId);

    /// <summary>The Region and validity of a short-term key; null for anything else (a long-term key is
    /// opaque, and garbage is garbage).</summary>
    public static KeyInfo? Describe(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        string url;
        try
        {
            url = Encoding.UTF8.GetString(Convert.FromBase64String(key[Prefix.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
        var query = url.IndexOf('?');
        if (query < 0) return null;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in url[(query + 1)..].Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0) parameters[pair[..equals]] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        // Credential = AKID/yyyyMMdd/region/bedrock/aws4_request
        if (!parameters.TryGetValue("X-Amz-Credential", out var credential)
            || credential.Split('/') is not [var accessKey, _, var region, _, _]
            || !parameters.TryGetValue("X-Amz-Date", out var date)
            || !DateTimeOffset.TryParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var issued)
            || !parameters.TryGetValue("X-Amz-Expires", out var expires)
            || !long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return null;
        return new KeyInfo(region, issued, issued.AddSeconds(seconds), accessKey);
    }
}
