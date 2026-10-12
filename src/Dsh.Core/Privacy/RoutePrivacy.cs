using System.Net;
using System.Net.Sockets;

namespace Dsh.Core;

// MARK: - Which routes are private
//
// A private route is a model on hardware the user controls — the PC itself, a box on the home network, a
// machine on their tailnet — and may see sensitive data as it is. Every other route gets sensitive details
// masked before anything is sent (see PrivacyShield). The default is decided by the address and errs towards
// "not private": an address that can't be placed is someone else's machine. (ProviderProfile.IsSelfHosted
// errs the other way, which is right for thinking parameters and wrong here.) The user can flip it either way.

public static class RoutePrivacy
{
    /// <summary>Name endings that only resolve inside a home or company network (or a tailnet).</summary>
    private static readonly string[] PrivateSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".ts.net"];

    /// <summary>Private by default: localhost; a loopback, RFC 1918, link-local, CGNAT (100.64.0.0/10 — Tailscale), or
    /// IPv6 unique-local or loopback address; a single-label host name; or a name ending in .local, .lan, .home,
    /// .home.arpa, .internal or .ts.net. Everything else (Amazon Bedrock and every hosted plan included) is not.</summary>
    public static bool IsPrivateAddress(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        if (host == "localhost") return true;
        if (IPAddress.TryParse(host, out var address)) return IsPrivate(address);
        if (!host.Contains('.')) return true; // a single-label name: "spark", "evo-x2"
        return PrivateSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>Loopback, RFC 1918, link-local, CGNAT, and IPv6 ULA/loopback/link-local.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC // fc00::/7, unique local
                   || address.IsIPv6LinkLocal;
        }
        return false;
    }
}
