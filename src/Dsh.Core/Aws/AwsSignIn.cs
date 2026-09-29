using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Signing in to AWS
//
// `aws login` (AWS CLI 2.32.0+) signs the CLI in with the same sign-in as the AWS console: it starts a
// local callback server, opens the browser at https://<region>.signin.aws.amazon.com/v1/authorize?…, and
// saves a refreshable session in the profile (login_session). What it prints, from
// awscli/customizations/sso/utils.py (OpenBrowserHandler / PrintOnlyHandler) and login/utils.py:
//
//   Attempting to open your default browser. If the browser does not open, open the following URL.
//   If you are unable to open the URL on this device, run this command again with the '--remote' option.
//
//   https://us-east-1.signin.aws.amazon.com/v1/authorize?response_type=code&client_id=...
//
// With --remote (no browser on this machine) it prints "Browser will not be automatically opened.
// Please visit the following URL:", the URL, then asks "Enter the authorization code displayed in your
// browser: " (no newline) and reads the code from standard input. When the profile already holds a
// different login session it asks "... Do you want to overwrite it to use <session> instead? (y/n): ".
// It prompts for a Region unless --region is given, refuses profiles configured with SSO, keys, a role,
// a web identity or credential_process, and — only when it asked for the Region — saves it to the
// profile. DSH always passes --region and then saves the Region itself (`aws configure set`), because the
// CLI refreshes the session later through that Region's sign-in endpoint.

/// <summary>Who AWS says is signed in (sts get-caller-identity).</summary>
public sealed record CallerIdentity(string Account, string Arn, string UserId)
{
    /// <summary>"1234-5678-9012", the way the AWS console shows account numbers.</summary>
    public string DisplayAccount => Account.Length == 12 && Account.All(char.IsAsciiDigit)
        ? $"{Account[..4]}-{Account[4..8]}-{Account[8..]}"
        : Account;

    /// <summary>The person or role: "alice" for arn:aws:iam::…:user/alice, "alice@example.com (role
    /// Developer)" for an assumed role, "the account's root user" for root.</summary>
    public string PrincipalName
    {
        get
        {
            var resource = Arn.Split(':', 6) is { Length: 6 } parts ? parts[5] : Arn;
            if (resource == "root") return "the account's root user";
            var segments = resource.Split('/');
            if (segments[0] == "assumed-role" && segments.Length >= 3) return $"{segments[^1]} (role {segments[1]})";
            return segments.Length >= 2 ? segments[^1] : resource;
        }
    }

    /// <summary>"Signed in to AWS account 1234-5678-9012 as alice."</summary>
    public string Description => $"Signed in to AWS account {DisplayAccount} as {PrincipalName}.";
}

/// <summary>The sign-in page the CLI wants the user to open, and the code to type there (device-code
/// sign-in to IAM Identity Center only).</summary>
public sealed record AwsSignInLink(Uri Url, string? UserCode = null);

public enum AwsSignInOutcome
{
    SignedIn,
    /// <summary>The user stopped it (declined to paste a code, or the CLI was interrupted).</summary>
    Cancelled,
    /// <summary>The profile signs in with IAM Identity Center: use <see cref="AwsSignIn.SsoLoginAsync"/>.</summary>
    UseSsoLogin,
    /// <summary>The profile already uses keys, a role, a web identity or a credential program; `aws login`
    /// won't touch it. Pick another profile name (DSH's own is "dsh-bedrock").</summary>
    ProfileHasOtherCredentials,
    /// <summary>The profile isn't set up for IAM Identity Center (for <see cref="AwsSignIn.SsoLoginAsync"/>).</summary>
    NotSsoProfile,
    /// <summary>The AWS CLI is too old for this sign-in.</summary>
    CliTooOld,
    /// <summary>The browser part wasn't finished in time (the CLI waits 10 minutes).</summary>
    TimedOut,
    /// <summary>The sign-in worked but this identity may not create CLI sessions.</summary>
    NotAllowed,
    Failed,
}

/// <summary>How a sign-in ended, in words for the guide.</summary>
public sealed record AwsSignInResult(AwsSignInOutcome Outcome, string Message, AwsSignInLink? Link = null, string? Details = null)
{
    public bool Succeeded => Outcome == AwsSignInOutcome.SignedIn;
}

/// <summary>Options for <see cref="AwsSignIn.LoginAsync"/> and <see cref="AwsSignIn.SsoLoginAsync"/>.</summary>
public sealed record AwsSignInOptions
{
    /// <summary>For a machine without a browser: the CLI prints the link and DSH asks the user for the
    /// code the browser shows afterwards (<see cref="RequestAuthorizationCode"/>).</summary>
    public bool Remote { get; init; }

    /// <summary>`aws login --redirect-port` (CLI 2.37.2+): a fixed port for the browser to return to.</summary>
    public int? RedirectPort { get; init; }

    /// <summary>Every line the CLI prints, as it arrives (background thread).</summary>
    public Action<string>? OnOutput { get; init; }

    /// <summary>The sign-in link as soon as the CLI prints it, so the guide can offer "Open the sign-in
    /// page again" (and show the code, for device-code SSO). Background thread.</summary>
    public Action<AwsSignInLink>? OnSignInLink { get; init; }

    /// <summary>Remote sign-in: ask the user for the authorization code the browser shows after they
    /// sign in. Return null to give up.</summary>
    public Func<AwsSignInLink, CancellationToken, Task<string?>>? RequestAuthorizationCode { get; init; }

    /// <summary>Answer to "Profile X is already configured to use session A. Do you want to overwrite it
    /// to use session B instead?" — the user asked to sign in, so yes by default.</summary>
    public bool ReplaceExistingSession { get; init; } = true;

    /// <summary>Safety limit on the whole sign-in (the CLI itself gives up after 10 minutes).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Signs the AWS CLI in (`aws login` or `aws sso login`) and checks who is signed in.</summary>
public sealed partial class AwsSignIn(AwsCli cli, Func<AwsProfiles>? loadProfiles = null)
{
    /// <summary>The profile DSH creates for itself, so it never disturbs profiles the user already has.</summary>
    public const string DefaultProfile = "dsh-bedrock";

    private readonly Func<AwsProfiles> _loadProfiles = loadProfiles ?? (() => AwsProfiles.Load());

    /// <summary>`aws login --profile P --region R [--remote] [--redirect-port N]`.</summary>
    public static IReadOnlyList<string> LoginArguments(string profile, string region, bool remote = false, int? redirectPort = null)
    {
        List<string> args = ["login", "--profile", profile, "--region", region];
        if (remote) args.Add("--remote");
        if (redirectPort is { } port) args.AddRange(["--redirect-port", port.ToString(CultureInfo.InvariantCulture)]);
        return args;
    }

    /// <summary>`aws sso login --profile P` (device code and no browser when remote).</summary>
    public static IReadOnlyList<string> SsoLoginArguments(string profile, bool remote = false)
    {
        List<string> args = ["sso", "login", "--profile", profile];
        if (remote) args.AddRange(["--use-device-code", "--no-browser"]);
        return args;
    }

    /// <summary>Sign in with the browser (`aws login`), into <paramref name="profile"/>, then make sure the
    /// profile remembers <paramref name="region"/>. Opens the user's browser; returns when they have
    /// signed in, given up, or the CLI's 10 minutes ran out. The profile is checked first: one already
    /// set up with other credentials is left alone and the result says what to do instead.</summary>
    public async Task<AwsSignInResult> LoginAsync(string profile = DefaultProfile, string region = BedrockRegions.Default,
                                                  AwsSignInOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        options ??= new AwsSignInOptions();
        var existing = SafeLoad()?.Find(profile);
        if (existing is { Kind: AwsProfileKind.Sso })
            return new(AwsSignInOutcome.UseSsoLogin,
                $"The AWS profile \"{profile}\" signs in through IAM Identity Center (SSO), so DSH will use the SSO sign-in for it.");
        if (existing is { CanUseAwsLogin: false })
            return new(AwsSignInOutcome.ProfileHasOtherCredentials,
                $"The AWS profile \"{profile}\" already uses {existing.KindDescription}, which the browser sign-in won't replace. Use it as it is, or let DSH sign in with its own profile (\"{DefaultProfile}\").");

        var result = await RunSignInAsync(LoginArguments(profile, region, options.Remote, options.RedirectPort), options, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded) return result;

        // `aws login --region R` saves R only when it had to ask for it; the session's later refreshes need it.
        if (SafeLoad()?.Find(profile) is not { Region: { Length: > 0 } })
        {
            var set = await cli.RunAsync(["configure", "set", "region", region, "--profile", profile],
                new AwsCliRunOptions { Timeout = TimeSpan.FromSeconds(60) }, cancellationToken).ConfigureAwait(false);
            if (!set.Succeeded)
                return result with { Message = $"Signed in, but DSH couldn't save the Region in the profile: {AwsCliErrors.CleanMessage(set.Stderr + set.Stdout)}" };
        }
        return result with { Message = "Signed in to AWS." };
    }

    /// <summary>Sign in to IAM Identity Center for a profile already set up with `aws configure sso`.</summary>
    public async Task<AwsSignInResult> SsoLoginAsync(string profile, AwsSignInOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        options ??= new AwsSignInOptions();
        if (SafeLoad() is { } profiles && profiles.Find(profile) is not { Kind: AwsProfileKind.Sso })
            return new(AwsSignInOutcome.NotSsoProfile,
                $"The AWS profile \"{profile}\" isn't set up for IAM Identity Center (SSO). Set it up with \"aws configure sso\", or sign in with the browser instead.");
        var result = await RunSignInAsync(SsoLoginArguments(profile, options.Remote), options, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result with { Message = "Signed in to AWS IAM Identity Center." } : result;
    }

    /// <summary>Who is signed in: `aws sts get-caller-identity`. Throws
    /// <see cref="AwsSignInRequiredException"/> when nobody is.</summary>
    public async Task<CallerIdentity> VerifyAsync(string profile = DefaultProfile, string? region = null,
                                                  CancellationToken cancellationToken = default)
    {
        var json = await cli.RunJsonAsync(["sts", "get-caller-identity"], profile, region, cancellationToken: cancellationToken).ConfigureAwait(false);
        var obj = json as JsonObject ?? new JsonObject();
        var account = JsonArgs.String(obj, "Account");
        var arn = JsonArgs.String(obj, "Arn");
        if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(arn))
            throw new AwsCliException(new AwsCliError(AwsCliFailure.Other, "AWS didn't say who is signed in.", Details: json.ToJsonString()));
        return new CallerIdentity(account, arn, JsonArgs.String(obj, "UserId") ?? "");
    }

    private AwsProfiles? SafeLoad()
    {
        try
        {
            return _loadProfiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<AwsSignInResult> RunSignInAsync(IReadOnlyList<string> args, AwsSignInOptions options, CancellationToken cancellationToken)
    {
        AwsSignInLink? link = null;
        var gaveUp = false;
        var keptOldSession = false;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnLine(AwsCliLine line)
        {
            options.OnOutput?.Invoke(line.Text);
            if (ExtractSignInUrl(line.Text) is { } url)
            {
                link = new AwsSignInLink(url, link?.UserCode);
                options.OnSignInLink?.Invoke(link);
            }
            else if (link is not null && UserCodeRegex().IsMatch(line.Text.Trim()))
            {
                link = link with { UserCode = line.Text.Trim() };
                options.OnSignInLink?.Invoke(link);
            }
        }

        async Task<string?> Answer(string prompt, CancellationToken token)
        {
            var text = prompt.Trim();
            if (text.EndsWith("(y/n):", StringComparison.OrdinalIgnoreCase) && text.Contains("overwrite", StringComparison.OrdinalIgnoreCase))
            {
                keptOldSession = !options.ReplaceExistingSession;
                return options.ReplaceExistingSession ? "y" : "n";
            }
            // The agent-toolkit question (CLI 2.36.50+) is switched off by environment; if it still comes, decline.
            if (text.EndsWith("[y/n/never]:", StringComparison.OrdinalIgnoreCase)) return "n";
            if (text.StartsWith("Enter the authorization code", StringComparison.OrdinalIgnoreCase) && text.EndsWith(':'))
            {
                string? code = null;
                if (options.RequestAuthorizationCode is { } ask && link is { } current) code = await ask(current, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(code))
                {
                    gaveUp = true;
                    await run.CancelAsync().ConfigureAwait(false);
                    return null;
                }
                return code.Trim();
            }
            return null;
        }

        AwsCliResult result;
        try
        {
            result = await cli.RunAsync(args, new AwsCliRunOptions { OnLine = OnLine, AnswerPrompt = Answer, Timeout = options.Timeout }, run.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (gaveUp && !cancellationToken.IsCancellationRequested)
        {
            return new(AwsSignInOutcome.Cancelled, "Sign-in stopped: no authorization code was entered.", link);
        }
        catch (IOException ex)
        {
            return new(AwsSignInOutcome.Failed, $"DSH couldn't start the AWS CLI: {ex.Message}", link);
        }
        // Answering "n" ends `aws login` successfully without touching the profile.
        if (result.Succeeded && keptOldSession)
            return new(AwsSignInOutcome.Cancelled, "Kept the existing AWS sign-in; the profile wasn't changed.", link);
        return Interpret(result, link);
    }

    /// <summary>What a finished `aws login` / `aws sso login` run means.</summary>
    public static AwsSignInResult Interpret(AwsCliResult result, AwsSignInLink? link = null)
    {
        if (result.Succeeded) return new(AwsSignInOutcome.SignedIn, "Signed in to AWS.", link);
        var text = result.Stderr + "\n" + result.Stdout;
        var details = AwsCliErrors.CleanMessage(result.Stderr.Trim().Length > 0 ? result.Stderr : result.Stdout);
        bool Has(string phrase) => text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
        if (result.TimedOut || Has("pending authorization to retrieve an SSO token has expired"))
            return new(AwsSignInOutcome.TimedOut, "The sign-in page wasn't completed in time. Start the sign-in again and finish it in the browser.", link, details);
        if (result.ExitCode == 130)
            return new(AwsSignInOutcome.Cancelled, "Sign-in stopped.", link, details);
        if (Has("is already configured with"))
            return new(AwsSignInOutcome.ProfileHasOtherCredentials,
                $"That AWS profile already has other credentials, which the browser sign-in won't replace. Let DSH sign in with its own profile (\"{DefaultProfile}\") instead.", link, details);
        if (Has("Found invalid choice") || Has("Invalid choice") || Has("Unknown options"))
            return new(AwsSignInOutcome.CliTooOld,
                $"This AWS CLI can't sign in this way yet (it needs version {AwsCli.MinimumVersion.ToString(3)} or newer). Update the AWS CLI — DSH can do it for you.", link, details);
        if (Has("Failed to retrieve an auth_code or state") || Has("Failed to decode the verification code") || Has("does not match expected value"))
            return new(AwsSignInOutcome.Failed, "That authorization code wasn't accepted. Copy the whole code the browser shows and try again.", link, details);
        if (Has("expired authorization code"))
            return new(AwsSignInOutcome.TimedOut, "The authorization code expired before it was used. Sign in again and paste the new code straight away.", link, details);
        if (Has("Unable to initialize the OAuth 2.0 authorization callback handler"))
            return new(AwsSignInOutcome.Failed,
                "The AWS CLI couldn't open a local port for the browser to come back to. Try again, choose another port, or use the sign-in for machines without a browser.", link, details);
        var error = AwsCliErrors.Classify(result, "signin");
        return error.Kind switch
        {
            AwsCliFailure.AccessDenied => new(AwsSignInOutcome.NotAllowed, error.Message, link, details),
            AwsCliFailure.Network => new(AwsSignInOutcome.Failed, error.Message, link, details),
            _ => new(AwsSignInOutcome.Failed, $"Sign-in didn't finish: {details}", link, details),
        };
    }

    /// <summary>The sign-in URL on a line of the CLI's output (it prints the URL alone on its line).</summary>
    public static Uri? ExtractSignInUrl(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || trimmed.Any(char.IsWhiteSpace)) return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    /// <summary>IAM Identity Center device codes look like "ABCD-EFGH".</summary>
    [GeneratedRegex(@"^[A-Z0-9]{4}-[A-Z0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserCodeRegex();
}
