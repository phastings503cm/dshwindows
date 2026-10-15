using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vision.Core;

// MARK: - Swapper connections
//
// A Spark Swapper box DSH knows about: a DGX Spark or a Strix Halo running Spark Swapper. There can be
// several. Each is reached either with an app token (from the connection code the Swapper's Connect tab
// shows, Swapper 2.0) or, for an older Swapper, with a username and password. The secret — token or
// password — lives in Windows Credential Manager under the connection's id, never in the settings file.

/// <summary>How DSH signs in to a Swapper.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SwapperAuth>))]
public enum SwapperAuth
{
    /// <summary>An app token from a connection code (Swapper 2.0).</summary>
    [JsonStringEnumMemberName("token")] Token,
    /// <summary>A username and password (any Swapper; the only way before 2.0).</summary>
    [JsonStringEnumMemberName("password")] Password,
}

/// <summary>One Swapper box, as saved in settings (no secrets).</summary>
public sealed record SwapperConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>What the box calls itself ("DGX Spark (spark-3f2a)"); the user may rename it.</summary>
    public string Name { get; set; } = "";
    /// <summary>The Swapper's web address: https://&lt;box&gt;:8999.</summary>
    public string Url { get; set; } = "";
    /// <summary>"nvidia-gb10", "amd-strix-halo" — null when the Swapper didn't say.</summary>
    public string? Platform { get; set; }
    public string? Hostname { get; set; }
    public SwapperAuth Auth { get; set; } = SwapperAuth.Token;
    /// <summary>The login's username (password auth), or the user a token was made for.</summary>
    public string? Username { get; set; }
    /// <summary>The certificate fingerprint ("AB:CD:…") DSH accepts from the Swapper and its model API.</summary>
    public string? PinnedFingerprint { get; set; }
    /// <summary>The box's model API (https://&lt;box&gt;:11443/v1), when known: routes with this base URL are this box's.</summary>
    public string? ApiBase { get; set; }
    /// <summary>Routes set up for this box (route ids).</summary>
    public List<string> RouteIds { get; set; } = [];

    /// <summary>"DGX Spark", "Strix Halo" — or null when the box hasn't said.</summary>
    [JsonIgnore] public string? PlatformLabel => SwapperPlatforms.Label(Platform);

    [JsonIgnore] public Uri? Address => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>Whether <paramref name="route"/> talks to this box's model API: linked to it, or at its address.</summary>
    public bool Serves(ProviderProfile route)
    {
        if (RouteIds.Contains(route.RouteId, StringComparer.Ordinal)) return true;
        if (ApiBase is { Length: > 0 } api && SameBase(api, route.BaseUrl)) return true;
        return false;
    }

    /// <summary>Two base URLs name the same endpoint (case, a trailing slash and a default port don't matter).</summary>
    public static bool SameBase(string a, string b)
    {
        if (!Uri.TryCreate(a.Trim(), UriKind.Absolute, out var x) || !Uri.TryCreate(b.Trim(), UriKind.Absolute, out var y))
            return string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        return string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)
               && x.Port == y.Port
               && string.Equals(x.AbsolutePath.TrimEnd('/'), y.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    public SwapperConnection DeepCopy() => this with { RouteIds = [.. RouteIds] };

    /// <summary>The Credential Manager entry that holds this connection's token or password.</summary>
    [JsonIgnore] public string SecretTarget => SecretStore.SwapperTarget(Id);
}

public static class SwapperConnections
{
    /// <summary>Before there could be several boxes, settings held one Swapper (Settings › DGX Spark): its address,
    /// username and pinned certificate, with the password in Credential Manager. That becomes the first connection,
    /// linked to the routes on the same machine. Null when there was nothing the user set up — an address the app
    /// guessed from the model route, with no login, is not a setup.</summary>
    public static SwapperConnection? MigrateLegacy(string? url, string? username, string? pinnedFingerprint, bool hasPassword,
                                                   IEnumerable<ProviderProfile> routes)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (string.IsNullOrWhiteSpace(username) && !hasPassword && string.IsNullOrWhiteSpace(pinnedFingerprint)) return null;
        var address = url.Trim().TrimEnd('/');
        var host = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : null;
        var linked = host is null
            ? []
            : routes.Where(r => Uri.TryCreate(r.BaseUrl, UriKind.Absolute, out var b) && string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return new SwapperConnection
        {
            Name = SparkSetup.ProviderName,
            Url = address,
            Auth = SwapperAuth.Password,
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            PinnedFingerprint = string.IsNullOrWhiteSpace(pinnedFingerprint) ? null : pinnedFingerprint,
            // The secure model address the setup guide makes is on the same host, port 11443.
            ApiBase = linked.FirstOrDefault(r => r.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))?.BaseUrl
                      ?? linked.FirstOrDefault()?.BaseUrl,
            RouteIds = linked.Select(r => r.RouteId).Distinct(StringComparer.Ordinal).ToList(),
        };
    }
}

// MARK: - Connection codes

/// <summary>A connection code from the Swapper's Connect tab ("Connect DSH"): <c>SSC1:</c> and base64url of a JSON
/// object that says where the box is, how to reach its model API, which certificate to expect, and an app token.</summary>
public sealed record SwapperConnectionCode
{
    public required string SwapperUrl { get; init; }
    public required string ApiBase { get; init; }
    /// <summary>"AB:CD:…" (32 bytes, upper case).</summary>
    public required string Fingerprint { get; init; }
    public required string Token { get; init; }
    public string Name { get; init; } = "";
    public string? Hostname { get; init; }
    public string? Platform { get; init; }
    /// <summary>The Swapper's version ("2.0.0").</summary>
    public string? Version { get; init; }
    public string? ApiKey { get; init; }
    /// <summary>What the box was serving when the code was made (null: nothing).</summary>
    public string? Model { get; init; }
    public int? Context { get; init; }
    /// <summary>The Swapper user the token belongs to.</summary>
    public string? User { get; init; }

    public const string Prefix = "SSC1:";

    /// <summary>What the app says when a code can't be read.</summary>
    public const string Invalid =
        "That doesn't look like a Spark Swapper connection code. Copy it again from the Swapper's Connect tab.";

    /// <summary>Read a pasted code: with or without the <c>SSC1:</c> prefix (any case), base64url or standard base64,
    /// padded or not, with line breaks or spaces anywhere — or the JSON object itself. Throws
    /// <see cref="FormatException"/> with <see cref="Invalid"/> when it isn't one.</summary>
    public static SwapperConnectionCode Parse(string text) =>
        TryParse(text, out var code) ? code! : throw new FormatException(Invalid);

    public static bool TryParse(string? text, out SwapperConnectionCode? code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        string json;
        if (trimmed.StartsWith('{'))
        {
            // The JSON itself (its values may hold spaces, so only the ends are trimmed).
            json = trimmed;
        }
        else
        {
            var compact = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (compact.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) compact = compact[Prefix.Length..];
            if (Base64(compact) is not { } bytes) return false;
            try
            {
                json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
        if (obj is null) return false;
        if (obj.TryGetPropertyValue("v", out var version) && version is not null
            && !(JsonNumbers.TryGetInt(version, out var v) && v == 1))
            return false;
        var swapper = JsonArgs.String(obj, "swapper_url")?.Trim();
        var api = JsonArgs.String(obj, "api_base")?.Trim();
        var token = JsonArgs.String(obj, "token")?.Trim();
        var fingerprint = StrictFingerprint(JsonArgs.String(obj, "fingerprint"));
        if (!IsHttps(swapper) || !IsHttps(api) || string.IsNullOrEmpty(token) || fingerprint is null) return false;
        var hostname = JsonArgs.String(obj, "hostname");
        var platform = JsonArgs.String(obj, "platform");
        code = new SwapperConnectionCode
        {
            SwapperUrl = swapper!.TrimEnd('/'),
            ApiBase = api!.TrimEnd('/'),
            Fingerprint = fingerprint,
            Token = token,
            Name = JsonArgs.String(obj, "name") is { Length: > 0 } name ? name.Trim() : DefaultName(platform, hostname, swapper),
            Hostname = hostname,
            Platform = platform,
            Version = JsonArgs.String(obj, "version"),
            ApiKey = JsonArgs.String(obj, "api_key"),
            Model = JsonArgs.String(obj, "model"),
            Context = JsonNumbers.Positive(obj, "context"),
            User = JsonArgs.String(obj, "user"),
        };
        return true;
    }

    /// <summary>"DGX Spark (spark-3f2a)" — what to call a box that didn't name itself.</summary>
    public static string DefaultName(string? platform, string? hostname, string? swapperUrl)
    {
        var kind = SwapperPlatforms.Label(platform) ?? "Spark Swapper";
        var host = hostname is { Length: > 0 } h ? h
            : Uri.TryCreate(swapperUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
        return host is { Length: > 0 } ? $"{kind} ({host})" : kind;
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0;

    /// <summary>Exactly 32 pairs of hex digits separated by colons (any case) — "AB:CD:…" upper case, or null.</summary>
    internal static string? StrictFingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length != 32 || parts.Any(p => p.Length != 2 || !p.All(Uri.IsHexDigit))) return null;
        return string.Join(':', parts.Select(p => p.ToUpperInvariant()));
    }

    /// <summary>base64url (RFC 4648 §5) or standard base64, with or without padding.</summary>
    private static byte[]? Base64(string text)
    {
        if (text.Length == 0) return null;
        var normal = text.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        if (normal.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/'))) return null;
        switch (normal.Length % 4)
        {
            case 1: return null;
            case 2: normal += "=="; break;
            case 3: normal += "="; break;
        }
        try
        {
            return Convert.FromBase64String(normal);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The code for <paramref name="fields"/> — what a Swapper writes (tests, and the self-test's sample).</summary>
    public static string Encode(JsonObject fields) =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(fields.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// MARK: - Addresses

public static partial class SwapperAddress
{
    /// <summary>The Swapper's address from what the user typed: a bare host or IP gets https:// and port 8999; a full
    /// address keeps its port; a path is dropped. Null when it isn't an address at all.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        // The Swapper only speaks HTTPS; a port typed by hand stays (even 443, which the parser calls "default").
        var port = uri.IsDefaultPort && !TypedPort().IsMatch(trimmed) ? SparkSetup.SwapperPort : uri.Port;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host.Trim('[', ']')}]" : uri.Host;
        return $"https://{host}:{port}";
    }

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9+.\-]*://)?(?:\[[^\]]*\]|[^/:?#\[\]]+):\d+(?:[/?#]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex TypedPort();

    /// <summary>"DSH on DESKTOP-1234" — the label a token gets, so the Swapper's Connect tab says whose it is.</summary>
    public static string TokenLabel(string? machineName = null) => $"DSH on {machineName ?? Environment.MachineName}";
}

// MARK: - /swap

/// <summary>What <c>/swap</c>'s argument asks for, with several boxes: which box (an index into the list) and which
/// model.</summary>
public sealed record SwapTarget(int? Box, string Model, bool Ambiguous);

public static class SwapTargets
{
    /// <summary>Read <c>/swap &lt;box&gt; &lt;model&gt;</c> or <c>/swap &lt;model&gt;</c>. A box is named by a prefix of its
    /// name or host name (any case, the longest that fits wins); without one, the model is for
    /// <paramref name="current"/> — the box behind the primary route — else the only box. With several boxes and
    /// none current, <see cref="SwapTarget.Ambiguous"/> says to ask which.</summary>
    public static SwapTarget Resolve(string argument, IReadOnlyList<(string Name, string? Hostname)> boxes, int? current)
    {
        var arg = argument.Trim();
        if (boxes.Count > 0)
        {
            // The longest box name the argument starts with, followed by a space and something more.
            var best = -1;
            var bestLength = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                foreach (var name in new[] { boxes[i].Name, boxes[i].Hostname })
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (arg.Length > name.Length && arg.StartsWith(name, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(arg[name.Length])
                        && name.Length > bestLength)
                    {
                        best = i;
                        bestLength = name.Length;
                    }
                }
            }
            if (best >= 0) return new SwapTarget(best, arg[bestLength..].Trim(), false);
            // A first word that starts exactly one box's name or host name.
            var space = arg.IndexOfAny([' ', '\t']);
            if (space > 0)
            {
                var word = arg[..space];
                var matches = Enumerable.Range(0, boxes.Count)
                    .Where(i => boxes[i].Name.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                                || (boxes[i].Hostname?.StartsWith(word, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
                if (matches.Count == 1) return new SwapTarget(matches[0], arg[space..].Trim(), false);
            }
        }
        if (current is { } index && index >= 0 && index < boxes.Count) return new SwapTarget(index, arg, false);
        if (boxes.Count == 1) return new SwapTarget(0, arg, false);
        return new SwapTarget(null, arg, boxes.Count > 1);
    }
}
