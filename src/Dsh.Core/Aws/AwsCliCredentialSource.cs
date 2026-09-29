using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - Credentials from the AWS CLI
//
// `aws configure export-credentials --format process` resolves a profile exactly as every AWS tool does —
// refreshing an `aws login` session or an IAM Identity Center token when it has to — and prints the
// result as the credential_process JSON (awscli/customizations/configure/exportcreds.py):
//   {"Version": 1, "AccessKeyId": "...", "SecretAccessKey": "...", "SessionToken": "...",
//    "Expiration": "2026-09-29T19:55:23.728587+00:00"}
// SessionToken and Expiration are absent for long-term access keys. DSH keeps the answer until five
// minutes before it expires, so a chat turn usually costs no CLI run at all.

/// <summary>AWS credentials for one CLI profile, fetched with `aws configure export-credentials` and
/// cached until shortly before they expire.</summary>
public sealed class AwsCliCredentialSource : IAwsCredentialSource
{
    private readonly AwsCli _cli;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private AwsCredentials? _cached;
    private TaskCompletionSource<AwsCredentials>? _inFlight;

    /// <param name="profile">The AWS CLI profile (e.g. "dsh-bedrock").</param>
    /// <param name="region">Passed as --region; null lets the profile's own Region apply. The CLI
    /// refreshes an `aws login` session through that Region's sign-in endpoint, so it must have one:
    /// <see cref="AwsSignIn.LoginAsync"/> saves the Region into the profile for this reason.</param>
    public AwsCliCredentialSource(AwsCli cli, string profile, string? region = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        _cli = cli;
        Profile = profile;
        Region = region;
        _time = time ?? TimeProvider.System;
    }

    public AwsCliCredentialSource(IAwsCliRunner runner, string profile, string? region = null, TimeProvider? time = null)
        : this(new AwsCli(runner), profile, region, time)
    {
    }

    public string Profile { get; }
    public string? Region { get; }

    /// <summary>Fetch again this long before the credentials expire.</summary>
    public TimeSpan RefreshMargin { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one export may take (a refresh is a network call to AWS).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The arguments DSH runs.</summary>
    public IReadOnlyList<string> ExportArguments =>
        AwsCli.Arguments(["configure", "export-credentials", "--format", "process"], Profile, Region, json: false);

    /// <summary>Cached credentials while they have more than <see cref="RefreshMargin"/> left; otherwise
    /// (or with <paramref name="forceRefresh"/>) a fresh export. Callers arriving while an export runs
    /// share it. Throws <see cref="AwsSignInRequiredException"/> when the user has to sign in again, and
    /// <see cref="AwsCliException"/> for other failures.</summary>
    public async Task<AwsCredentials> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<AwsCredentials> pending;
        var start = false;
        lock (_lock)
        {
            if (!forceRefresh && _inFlight is null && _cached is { } cached && !cached.ExpiresWithin(RefreshMargin, _time.GetUtcNow()))
                return cached;
            if (_inFlight is null)
            {
                _inFlight = new TaskCompletionSource<AwsCredentials>(TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
            pending = _inFlight;
        }
        // One caller giving up (its token cancelled) doesn't stop the export the others are waiting for.
        if (start) _ = ExportAsync(pending);
        return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forget the cached credentials (after signing in again, or out).</summary>
    public void Invalidate()
    {
        lock (_lock) _cached = null;
    }

    private async Task ExportAsync(TaskCompletionSource<AwsCredentials> pending)
    {
        try
        {
            var args = ExportArguments;
            var result = await _cli.Runner.RunAsync(args, new AwsCliRunOptions { Timeout = Timeout }, CancellationToken.None).ConfigureAwait(false);
            if (!result.Succeeded) throw AwsCliErrors.ToException(AwsCliErrors.Classify(result, "configure"), Profile);
            var credentials = ParseProcessOutput(result.Stdout, Profile);
            lock (_lock)
            {
                _cached = credentials;
                _inFlight = null;
            }
            pending.SetResult(credentials);
        }
        catch (Exception ex)
        {
            lock (_lock) _inFlight = null;
            pending.SetException(ex);
        }
    }

    /// <summary>Read the credential_process JSON. Expiration is ISO 8601 (Python's isoformat, e.g.
    /// "2026-09-29T19:55:23.728587+00:00", or "…Z").</summary>
    public static AwsCredentials ParseProcessOutput(string json, string profile = "default")
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            obj = null;
        }
        var accessKey = obj is null ? null : JsonArgs.String(obj, "AccessKeyId");
        var secret = obj is null ? null : JsonArgs.String(obj, "SecretAccessKey");
        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secret))
        {
            // Nothing usable came back: treat it as "not signed in" rather than a crash.
            throw new AwsSignInRequiredException(profile, "The AWS CLI didn't return credentials for this profile. Sign in again.");
        }
        var token = JsonArgs.String(obj!, "SessionToken");
        DateTimeOffset? expiration = null;
        if (JsonArgs.String(obj!, "Expiration") is { Length: > 0 } text)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                throw new AwsCliException(new AwsCliError(AwsCliFailure.Other, $"The AWS CLI returned an expiry time DSH couldn't read ({text}).", Details: text));
            expiration = parsed;
        }
        return new AwsCredentials(accessKey, secret, string.IsNullOrEmpty(token) ? null : token, expiration);
    }
}
