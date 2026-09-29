namespace Dsh.Core;

// MARK: - AWS credentials: the contract between the Bedrock client and where credentials come from
//
// DSH signs Bedrock requests itself (SigV4) with short-lived credentials. In the app they come from
// the AWS CLI (`aws configure export-credentials`) after `aws login` / `aws sso login`, so the CLI owns
// sign-in and refresh; tests hand in fixed ones.

/// <summary>A set of AWS credentials. <see cref="SessionToken"/> is set for temporary credentials
/// (every sign-in DSH does produces those).</summary>
public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null,
                                    DateTimeOffset? Expiration = null)
{
    /// <summary>True when the credentials expire within <paramref name="margin"/> (default 5 minutes).</summary>
    public bool ExpiresWithin(TimeSpan? margin = null, DateTimeOffset? now = null) =>
        Expiration is { } expiry && expiry - (now ?? DateTimeOffset.UtcNow) <= (margin ?? TimeSpan.FromMinutes(5));

    /// <summary>Never print the secret.</summary>
    public override string ToString() => $"AwsCredentials {{ AccessKeyId = {AccessKeyId}, Expiration = {Expiration} }}";
}

/// <summary>Where a Bedrock client gets credentials. Implementations cache and refresh them;
/// <paramref name="forceRefresh"/> is passed after AWS rejected the current ones as expired.</summary>
public interface IAwsCredentialSource
{
    Task<AwsCredentials> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>Fixed credentials (tests, or keys the user typed in).</summary>
public sealed class StaticAwsCredentialSource(AwsCredentials credentials) : IAwsCredentialSource
{
    public Task<AwsCredentials> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(credentials);
}

/// <summary>The AWS sign-in is missing or has run out (the CLI's refresh token expired, or the profile
/// was never signed in). Not retryable: the user has to sign in again, which opens their browser.</summary>
public sealed class AwsSignInRequiredException(string profile, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The AWS CLI profile that needs signing in.</summary>
    public string Profile { get; } = profile;
}
