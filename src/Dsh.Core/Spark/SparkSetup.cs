using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dsh.Core;

// MARK: - Spark addresses and the DSH route
//
// Where things live on a DGX Spark running Spark Swapper, and the provider route the setup guide
// saves: the model API through the Spark's HTTPS front (nginx on :11443), with the Swapper's
// self-signed certificate pinned and its model API key.

public static class SparkSetup
{
    public const int SshPort = 22;
    public const int SwapperPort = 8999;
    public const int ModelFrontPort = 11443;
    public const string ProviderName = "DGX Spark";

    /// <summary>"[fe80::1]" for IPv6 literals, the text otherwise.</summary>
    public static string UrlHost(string host)
    {
        var trimmed = host.Trim().Trim('[', ']');
        return IPAddress.TryParse(trimmed, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{trimmed}]" : trimmed;
    }

    /// <summary>The host part of whatever the user typed: "https://192.168.1.42:8999/" → "192.168.1.42".</summary>
    public static string HostOf(string text)
    {
        var trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Host.Length > 0) return uri.Host.Trim('[', ']');
        if (Uri.TryCreate("ssh://" + trimmed, UriKind.Absolute, out var bare) && bare.Host.Length > 0) return bare.Host.Trim('[', ']');
        return trimmed;
    }

    public static Uri SwapperUrl(string host) => new($"https://{UrlHost(host)}:{SwapperPort}");

    public static string ModelBaseUrl(string host) => $"https://{UrlHost(host)}:{ModelFrontPort}/v1";

    /// <summary>The route DSH uses to talk to the Spark's model.</summary>
    public static ProviderProfile Provider(string host, string model, string? apiKey, string? pinnedCertificate) =>
        new(ProviderKind.OpenAICompat, ProviderName, ModelBaseUrl(host), model)
        {
            ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
            PinnedCertificate = pinnedCertificate,
        };

    /// <summary>Send one tiny prompt and return the reply — proof the whole path works (address,
    /// certificate, key, model).</summary>
    public static async Task<string> SayHelloAsync(ILlmClient client, string model, CancellationToken cancellationToken = default)
    {
        var request = new LlmRequest(
            "You are a helpful assistant.",
            [LlmMessage.User("Say hello to a new DGX Spark owner in one short, friendly sentence.")],
            [], model, Temperature: 0.3, MaxTokens: 200, Thinking: ThinkingLevel.Off);
        var reply = new StringBuilder();
        await foreach (var e in client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (e is LlmStreamEvent.Text text) reply.Append(text.Delta);
        var result = reply.ToString().Trim();
        return result.Length == 0 ? "(The model answered with an empty reply.)" : result;
    }
}
