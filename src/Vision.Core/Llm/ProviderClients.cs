namespace Vision.Core;

// MARK: - Provider clients
//
// Every configured route is served by one of two clients: Bedrock profiles by BedrockClient (the
// Converse API, SigV4 or a Bedrock API key), everything else by OpenAiClient (chat completions). The
// app asks for a client here instead of choosing one itself — and gets it wrapped in the privacy shield
// (see ShieldedClient) when the route isn't private and the shield is on.

/// <summary>A streaming client bound to one provider profile, with the model probe the app uses to size
/// the context gauge.</summary>
public interface IProviderClient : ILlmClient
{
    ProviderProfile Profile { get; }

    /// <summary>The route's model metadata (context window, the id actually served, ...).</summary>
    Task<ModelInfo> ModelInfoAsync(string? model = null, CancellationToken cancellationToken = default);
}

public static class ProviderClients
{
    /// <summary>Where Bedrock profiles signed with AWS credentials get them; the app sets this at startup
    /// to its AWS-CLI-backed source. Null (or a profile with neither an AWS profile nor an API key)
    /// leaves Bedrock clients without credentials: their calls fail with a request to sign in.</summary>
    public static Func<ProviderProfile, IAwsCredentialSource>? AwsCredentials { get; set; }

    /// <summary>The client for <paramref name="profile"/>. <paramref name="handler"/> replaces the
    /// network (tests). With <paramref name="shield"/> switched on, a route that isn't private gets its
    /// requests masked (a private route never does). <paramref name="notice"/> hears the one line a client says
    /// when it adapted to a parameter the server refused (it was dropped, remembered, and the turn carried on).</summary>
    public static IProviderClient Create(ProviderProfile profile, HttpMessageHandler? handler = null, ShieldOptions? shield = null,
                                         Action<string>? notice = null)
    {
        var client = Raw(profile, handler, notice);
        return shield is { Enabled: true } && !profile.IsPrivateRoute ? new ShieldedClient(client, shield) : client;
    }

    private static IProviderClient Raw(ProviderProfile profile, HttpMessageHandler? handler, Action<string>? notice)
    {
        if (profile.Kind != ProviderKind.Bedrock) return new OpenAiClient(profile, handler, onNotice: notice);
        var usesApiKey = profile.AwsProfile is null && !string.IsNullOrEmpty(profile.ApiKey);
        return new BedrockClient(profile, usesApiKey ? null : AwsCredentials?.Invoke(profile), handler);
    }
}
