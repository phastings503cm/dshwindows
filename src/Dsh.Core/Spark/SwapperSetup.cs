namespace Dsh.Core;

// MARK: - Setting up a Swapper connection
//
// The two flows the wizard, the DGX Spark guide and Settings share: signing in with an address and a login
// (which, on Swapper 2.0, trades the login for an app token so the password is never kept), and installing a
// catalog model and following it until it is done.

/// <summary>A Swapper connection made from a login: the connection, its secret (an app token on Swapper 2.0, else the
/// password), and what the Swapper said about itself.</summary>
public sealed record SwapperLoginResult(SwapperConnection Connection, string Secret, SwapperConnectInfo? Connect,
                                        SwapperCredentials? Credentials)
{
    public bool UsesToken => Connection.Auth == SwapperAuth.Token;
}

public static class SwapperSetupFlows
{
    /// <summary>Sign in to the Swapper at <paramref name="url"/> (its certificate pinned to <paramref name="fingerprint"/>).
    /// A Swapper 2.0 hands out an app token, labelled <paramref name="tokenLabel"/>, which is kept instead of the password;
    /// an older one keeps the password, and the model API's address and key come from /api/credentials. Throws
    /// <see cref="SwapperException"/> (a wrong login is <see cref="SwapperErrorKind.BadLogin"/>).</summary>
    public static async Task<SwapperLoginResult> SignInAsync(Uri url, string username, string password, string? fingerprint,
                                                             string tokenLabel, CancellationToken cancellationToken = default)
    {
        using var client = new SparkSwapperClient(url, username, password, fingerprint);
        await client.LoginAsync(cancellationToken).ConfigureAwait(false);
        var session = await client.SessionAsync(cancellationToken).ConfigureAwait(false);
        var address = url.ToString().TrimEnd('/');
        if (await client.CreateTokenAsync(tokenLabel, cancellationToken).ConfigureAwait(false) is { } grant)
        {
            var connect = grant.Connect;
            var connection = new SwapperConnection
            {
                Name = connect?.Name ?? SwapperConnectionCode.DefaultName(session.Platform, session.Hostname, address),
                Url = address,
                Platform = connect?.Platform ?? session.Platform,
                Hostname = connect?.Hostname ?? session.Hostname,
                Auth = SwapperAuth.Token,
                Username = username,
                // The certificate this sign-in actually saw is the one to keep.
                PinnedFingerprint = fingerprint ?? connect?.Fingerprint,
                ApiBase = connect?.ApiBase,
            };
            return new SwapperLoginResult(connection, grant.Token, connect, null);
        }
        var credentials = await client.CredentialsAsync(cancellationToken).ConfigureAwait(false);
        var legacy = new SwapperConnection
        {
            Name = SwapperConnectionCode.DefaultName(session.Platform, session.Hostname, address),
            Url = address,
            Platform = session.Platform,
            Hostname = session.Hostname,
            Auth = SwapperAuth.Password,
            Username = username,
            PinnedFingerprint = fingerprint,
            ApiBase = credentials.ApiBaseUrl,
        };
        return new SwapperLoginResult(legacy, password, null, credentials);
    }

    /// <summary>Install the catalog model <paramref name="key"/> and follow the install until it ends, telling
    /// <paramref name="progress"/> about every poll. Returns the last state of the job (done or failed). A 409 (another
    /// install is running) and other refusals are thrown.</summary>
    public static async Task<SwapperJob?> InstallModelAsync(SparkSwapperClient client, string key, Action<SwapperJob>? progress = null,
                                                            TimeSpan? interval = null, CancellationToken cancellationToken = default)
    {
        var job = await client.InstallModelAsync(key, cancellationToken).ConfigureAwait(false);
        if (job is not null) progress?.Invoke(job);
        while (job is { IsRunning: true })
        {
            await Task.Delay(interval ?? TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
            var polled = await client.ProvisioningJobAsync(cancellationToken).ConfigureAwait(false);
            // Another job may have started since (a second window); ours has ended then.
            if (polled is null || polled.Id != job.Id) break;
            job = polled;
            progress?.Invoke(job);
        }
        return job;
    }

    /// <summary>"takes about a minute" — how long switching to <paramref name="model"/> takes, in words.</summary>
    public static string SwitchTime(SwapperModel model) => model.Eta is { Length: > 0 } eta ? eta : "a few minutes";
}
