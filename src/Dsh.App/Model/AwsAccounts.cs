using Dsh.Core;

namespace Dsh.App.Model;

/// <summary>Where Bedrock routes get AWS credentials in the app: the AWS CLI's sign-in for the route's
/// profile (`aws login` / `aws sso login`), exported on demand and cached until shortly before they
/// expire. One source per profile, shared by every chat, so a refresh happens once.</summary>
public static class AwsAccounts
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, IAwsCredentialSource> Sources = new(StringComparer.Ordinal);

    /// <summary>Hook the Bedrock client up to the AWS CLI. Called once at startup.</summary>
    public static void Install() => ProviderClients.AwsCredentials = CredentialsFor;

    /// <summary>The AWS CLI as installed now (it may have been installed while the app was running, so
    /// this looks again every time), or null.</summary>
    public static AwsCli? Cli() => AwsCli.Locate() is { } path ? AwsCli.ForPath(path) : null;

    public static IAwsCredentialSource CredentialsFor(ProviderProfile profile)
    {
        var name = profile.AwsProfile ?? AwsSignIn.DefaultProfile;
        lock (Gate)
        {
            if (Sources.TryGetValue(name, out var existing)) return existing;
            IAwsCredentialSource source = Cli() is { } cli
                ? new AwsCliCredentialSource(cli, name)
                : new MissingCli(name);
            // A missing CLI isn't cached: installing it later should just work.
            if (source is AwsCliCredentialSource) Sources[name] = source;
            return source;
        }
    }

    /// <summary>Forget cached credentials after a new sign-in (or a changed profile).</summary>
    public static void Invalidate(string? profile = null)
    {
        lock (Gate)
        {
            if (profile is null)
            {
                foreach (var each in Sources.Values.OfType<AwsCliCredentialSource>()) each.Invalidate();
                Sources.Clear();
                return;
            }
            if (Sources.Remove(profile, out var removed) && removed is AwsCliCredentialSource cli) cli.Invalidate();
        }
    }

    /// <summary>Sign <paramref name="profile"/> in again after its session ran out: the browser sign-in
    /// (`aws login`), or IAM Identity Center's for an SSO profile. Opens the user's browser.</summary>
    public static async Task<AwsSignInResult> SignInAgainAsync(string profile, string? region, CancellationToken cancellationToken = default)
    {
        if (Cli() is not { } cli)
            return new(AwsSignInOutcome.Failed, "The AWS CLI isn't installed on this PC any more. Run the Amazon Bedrock setup again (Settings › Models) — it reinstalls it.");
        var signIn = new AwsSignIn(cli);
        var result = await signIn.LoginAsync(profile, region ?? BedrockRegions.Default, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Outcome == AwsSignInOutcome.UseSsoLogin)
            result = await signIn.SsoLoginAsync(profile, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) Invalidate(profile);
        return result;
    }

    /// <summary>Stands in when the AWS CLI can't be found: every request asks for the setup again.</summary>
    private sealed class MissingCli(string profile) : IAwsCredentialSource
    {
        public Task<AwsCredentials> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            throw new AwsSignInRequiredException(profile,
                "The AWS CLI isn't installed on this PC any more, so DSH can't sign in to AWS. Run the Amazon Bedrock setup again (Settings › Models) — it reinstalls it.");
    }
}
