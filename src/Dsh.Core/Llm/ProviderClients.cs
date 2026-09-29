namespace Dsh.Core;

// MARK: - Provider clients
//
// Every configured route is served by one of two clients: Bedrock profiles by BedrockClient (the
// Converse API, SigV4 or a Bedrock API key), everything else by OpenAiClient (chat completions). The
// app asks for a client here instead of choosing one itself.

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
    /// network (tests).</summary>
    public static IProviderClient Create(ProviderProfile profile, HttpMessageHandler? handler = null)
    {
        if (profile.Kind != ProviderKind.Bedrock) return new OpenAiClient(profile, handler);
        var usesApiKey = profile.AwsProfile is null && !string.IsNullOrEmpty(profile.ApiKey);
        return new BedrockClient(profile, usesApiKey ? null : AwsCredentials?.Invoke(profile), handler);
    }
}
