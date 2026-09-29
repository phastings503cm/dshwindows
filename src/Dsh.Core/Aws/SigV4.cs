using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Dsh.Core;

// MARK: - AWS Signature Version 4
//
// DSH signs its own Bedrock requests instead of carrying the AWS SDK: SigV4 is a few hashes over a
// canonical form of the request, and the AWS SDK for .NET would add megabytes for two endpoints.
// The canonical form follows botocore exactly (the reference implementation the tests were checked
// against): every header named x-amz-* is signed along with host and content-type; the path is
// normalised (no dot segments, no empty segments) and percent-encoded a second time, so a model id
// sent as ".../v1%3A0/..." is signed as ".../v1%253A0/..."; query parameters are decoded, re-encoded
// with the RFC 3986 unreserved set and sorted by key, then value.

public static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>Hex SHA-256 of an empty payload (GETs, presigned URLs).</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Longest a presigned URL may live (AWS's own limit).</summary>
    public static readonly TimeSpan MaxPresignLifetime = TimeSpan.FromDays(7);

    /// <summary>Percent-encode everything but the RFC 3986 unreserved characters (A–Z a–z 0–9 - _ . ~),
    /// UTF-8 first, upper-case hex — botocore's <c>percent_encode</c>. This is how a path parameter such
    /// as a Bedrock model id or inference-profile ARN goes into a URL: ':' → %3A, '/' → %2F.</summary>
    public static string Encode(string value, bool keepSlash = false)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var output = new StringBuilder(bytes.Length * 3 / 2);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~'
                || (keepSlash && c == '/'))
                output.Append(c);
            else
                output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    /// <summary>The "20260929T123456Z" timestamp SigV4 uses.</summary>
    public static string AmzDate(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Sign <paramref name="request"/> in place with an Authorization header, adding X-Amz-Date
    /// and (for temporary credentials) X-Amz-Security-Token. <paramref name="body"/> must be exactly the
    /// bytes the request sends. Safe to call again on a retry: earlier auth headers are replaced.
    /// <paramref name="contentSha256"/> also sends the payload hash as X-Amz-Content-SHA256 (S3 wants
    /// it; Bedrock doesn't).</summary>
    public static void Sign(HttpRequestMessage request, ReadOnlySpan<byte> body, AwsCredentials credentials,
                            string region, string service, DateTimeOffset now, bool contentSha256 = false)
    {
        var url = request.RequestUri ?? throw new ArgumentException("The request has no URL.", nameof(request));
        var amzDate = AmzDate(now);
        var payloadHash = Hex(SHA256.HashData(body));
        foreach (var name in new[] { "Authorization", "X-Amz-Date", "X-Amz-Security-Token", "X-Amz-Content-SHA256" })
            request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        if (!string.IsNullOrEmpty(credentials.SessionToken))
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", credentials.SessionToken);
        if (contentSha256) request.Headers.TryAddWithoutValidation("X-Amz-Content-SHA256", payloadHash);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = Host(url) };
        foreach (var (name, values) in request.Headers)
        {
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("x-amz-", StringComparison.Ordinal)) headers[lower] = HeaderValue(values);
        }
        if (request.Content?.Headers.ContentType is { } contentType) headers["content-type"] = HeaderValue([contentType.ToString()]);

        var signedHeaders = string.Join(';', headers.Keys);
        var canonical = CanonicalRequest(request.Method.Method, url, CanonicalQuery(Parameters(url)), headers, payloadHash);
        var scope = Scope(amzDate, region, service);
        var signature = Signature(credentials.SecretAccessKey, amzDate, region, service, canonical);
        request.Headers.Authorization = new AuthenticationHeaderValue(Algorithm,
            $"Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>A presigned URL (query-string auth, as botocore's SigV4QueryAuth builds it): the URL's
    /// own parameters, then X-Amz-Algorithm, -Credential, -Date, -Expires, -SignedHeaders and, for
    /// temporary credentials, -Security-Token, then X-Amz-Signature — in that order, which matters
    /// because a Bedrock API key embeds the URL text. Only the host header is signed and the payload
    /// hash is that of an empty body. Returned as text: the exact characters are what a Bedrock API key
    /// carries, and <see cref="Uri"/> would re-canonicalise them.</summary>
    public static string Presign(HttpMethod method, Uri url, AwsCredentials credentials, string region, string service,
                              DateTimeOffset now, TimeSpan expires)
    {
        if (expires <= TimeSpan.Zero || expires > MaxPresignLifetime)
            throw new ArgumentOutOfRangeException(nameof(expires), "A presigned URL lives between 1 second and 7 days.");
        var amzDate = AmzDate(now);
        var scope = Scope(amzDate, region, service);
        var parameters = Parameters(url).ToList();
        parameters.Add(("X-Amz-Algorithm", Algorithm));
        parameters.Add(("X-Amz-Credential", $"{credentials.AccessKeyId}/{scope}"));
        parameters.Add(("X-Amz-Date", amzDate));
        parameters.Add(("X-Amz-Expires", ((long)expires.TotalSeconds).ToString(CultureInfo.InvariantCulture)));
        parameters.Add(("X-Amz-SignedHeaders", "host"));
        if (!string.IsNullOrEmpty(credentials.SessionToken)) parameters.Add(("X-Amz-Security-Token", credentials.SessionToken));

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = Host(url) };
        var canonical = CanonicalRequest(method.Method, url, CanonicalQuery(parameters), headers, EmptyPayloadHash);
        var signature = Signature(credentials.SecretAccessKey, amzDate, region, service, canonical);
        var query = string.Join('&', parameters.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
        return $"{url.Scheme}://{url.Authority}{url.AbsolutePath}?{query}&X-Amz-Signature={signature}";
    }

    // MARK: Canonical form

    internal static string CanonicalRequest(string method, Uri url, string canonicalQuery,
                                            IReadOnlyDictionary<string, string> headers, string payloadHash)
    {
        var text = new StringBuilder();
        text.Append(method.ToUpperInvariant()).Append('\n');
        text.Append(CanonicalPath(url.AbsolutePath)).Append('\n');
        text.Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in headers.OrderBy(h => h.Key, StringComparer.Ordinal))
            text.Append(name).Append(':').Append(value).Append('\n');
        text.Append('\n');
        text.Append(string.Join(';', headers.Keys.OrderBy(k => k, StringComparer.Ordinal))).Append('\n');
        text.Append(payloadHash);
        return text.ToString();
    }

    /// <summary>The path as sent (already percent-encoded), with dot and empty segments removed, then
    /// encoded once more — AWS signs every service but S3 over the doubly-encoded path.</summary>
    internal static string CanonicalPath(string path)
    {
        if (path.Length == 0) return "/";
        var output = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (output.Count > 0) output.RemoveAt(output.Count - 1);
                continue;
            }
            output.Add(segment);
        }
        var normalised = (path[0] == '/' ? "/" : "") + string.Join('/', output)
                         + (path[^1] == '/' && output.Count > 0 ? "/" : "");
        return Encode(normalised, keepSlash: true);
    }

    /// <summary>The URL's query parameters, decoded, in order.</summary>
    internal static IEnumerable<(string Key, string Value)> Parameters(Uri url)
    {
        var query = url.Query.TrimStart('?');
        if (query.Length == 0) yield break;
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? "" : pair[(equals + 1)..];
            yield return (Uri.UnescapeDataString(key), Uri.UnescapeDataString(value));
        }
    }

    internal static string CanonicalQuery(IEnumerable<(string Key, string Value)> parameters) =>
        string.Join('&', parameters
            .Select(p => (Key: Encode(p.Key), Value: Encode(p.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

    /// <summary>host[:port], the port only when it isn't the scheme's default.</summary>
    internal static string Host(Uri url) =>
        url.IsDefaultPort ? url.IdnHost.ToLowerInvariant() : $"{url.IdnHost.ToLowerInvariant()}:{url.Port}";

    /// <summary>Values joined by commas, each trimmed with inner runs of spaces collapsed.</summary>
    private static string HeaderValue(IEnumerable<string> values) =>
        string.Join(',', values.Select(v => string.Join(' ', v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));

    private static string Scope(string amzDate, string region, string service) =>
        $"{amzDate[..8]}/{region}/{service}/aws4_request";

    private static string Signature(string secret, string amzDate, string region, string service, string canonicalRequest)
    {
        var stringToSign = $"{Algorithm}\n{amzDate}\n{Scope(amzDate, region, service)}\n" +
                           Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), amzDate[..8]);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        return Hex(Hmac(key, stringToSign));
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
