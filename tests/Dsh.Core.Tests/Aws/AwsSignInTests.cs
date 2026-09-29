namespace Dsh.Core.Tests;

public sealed class AwsSignInTests
{
    // What `aws login` prints, captured from AWS CLI 2.37.5 (state and challenge shortened).
    private const string SameDeviceUrl = "https://eu-central-1.signin.aws.amazon.com/v1/authorize?response_type=code&client_id=arn%3Aaws%3Asignin%3A%3A%3Adevtools%2Fsame-device&state=89734afa-92db-48ad-81a0-af765322efcd&code_challenge_method=SHA-256&scope=openid&redirect_uri=http%3A%2F%2F127.0.0.1%3A53123%2Foauth%2Fcallback&code_challenge=abc";
    private const string RemoteUrl = "https://us-east-1.signin.aws.amazon.com/v1/authorize?response_type=code&client_id=arn%3Aaws%3Asignin%3A%3A%3Adevtools%2Fcross-device&state=4c6c7daf-2f1b-42c9-9d36-a99147ed34c8&code_challenge_method=SHA-256&scope=openid&redirect_uri=https%3A%2F%2Fus-east-1.signin.aws.amazon.com%2Fv1%2Fsessions%2Fconfirmation&code_challenge=abc";

    private static IReadOnlyList<FakeCliStep> SameDeviceOutput(params FakeCliStep[] then) =>
    [
        new FakeCliStep.Line("Attempting to open your default browser. If the browser does not open, open the following URL."),
        new FakeCliStep.Line("If you are unable to open the URL on this device, run this command again with the '--remote' option."),
        new FakeCliStep.Line(""),
        new FakeCliStep.Line(SameDeviceUrl),
        new FakeCliStep.Line(""),
        .. then,
    ];

    private static readonly AwsCliResult LoggedIn = new(0,
        "\nUpdated profile dsh-bedrock to use arn:aws:iam::111122223333:user/alice credentials.\n" +
        "Use \"--profile dsh-bedrock\" to use the new credentials, such as \"aws sts get-caller-identity --profile dsh-bedrock\"\n", "");

    private static Func<AwsProfiles> Profiles(string? config, string? credentials = null) => () => AwsProfiles.Parse(config, credentials);

    [Fact]
    public void BuildsTheLoginCommand()
    {
        Assert.Equal(["login", "--profile", "dsh-bedrock", "--region", "eu-central-1"], AwsSignIn.LoginArguments("dsh-bedrock", "eu-central-1"));
        Assert.Equal(["login", "--profile", "p", "--region", "us-east-1", "--remote", "--redirect-port", "8400"],
            AwsSignIn.LoginArguments("p", "us-east-1", remote: true, redirectPort: 8400));
        Assert.Equal(["sso", "login", "--profile", "corp-dev"], AwsSignIn.SsoLoginArguments("corp-dev"));
        Assert.Equal(["sso", "login", "--profile", "corp-dev", "--use-device-code", "--no-browser"], AwsSignIn.SsoLoginArguments("corp-dev", remote: true));
        Assert.Equal("dsh-bedrock", AwsSignIn.DefaultProfile);
    }

    [Fact]
    public async Task SignsInReportsTheLinkAndSavesTheRegion()
    {
        var runner = new FakeAwsCliRunner()
            .OnInteractive(["login"], SameDeviceOutput(), LoggedIn)
            .On(["configure", "set"], "");
        var loads = 0;
        // Before: no such profile. After: aws login wrote login_session but no region (it only saves one it asked for).
        var signIn = new AwsSignIn(new AwsCli(runner), () => loads++ == 0
            ? AwsProfiles.Parse("[default]\nregion = us-west-2\n", null)
            : AwsProfiles.Parse("[profile dsh-bedrock]\nlogin_session = arn:aws:iam::111122223333:user/alice\n", null));
        var output = new List<string>();
        var links = new List<AwsSignInLink>();

        var result = await signIn.LoginAsync("dsh-bedrock", "eu-central-1", new AwsSignInOptions
        {
            OnOutput = output.Add,
            OnSignInLink = links.Add,
        });

        Assert.True(result.Succeeded);
        Assert.Equal("Signed in to AWS.", result.Message);
        Assert.Equal(new Uri(SameDeviceUrl), result.Link!.Url);
        Assert.Equal(new Uri(SameDeviceUrl), Assert.Single(links).Url);
        Assert.Contains("Attempting to open your default browser. If the browser does not open, open the following URL.", output);
        var login = Assert.Single(runner.CallsTo("login"));
        Assert.Equal(["login", "--profile", "dsh-bedrock", "--region", "eu-central-1"], login.Args);
        Assert.Equal(TimeSpan.FromMinutes(15), login.Options!.Timeout);
        Assert.Equal(["configure", "set", "region", "eu-central-1", "--profile", "dsh-bedrock"], Assert.Single(runner.CallsTo("configure")).Args);
    }

    [Fact]
    public async Task ARegionAlreadyInTheProfileIsLeftAlone()
    {
        var runner = new FakeAwsCliRunner().OnInteractive(["login"], SameDeviceOutput(), LoggedIn);
        var signIn = new AwsSignIn(new AwsCli(runner), Profiles("[profile dsh-bedrock]\nlogin_session = old\nregion = eu-central-1\n"));
        Assert.True((await signIn.LoginAsync("dsh-bedrock", "eu-central-1")).Succeeded);
        Assert.Empty(runner.CallsTo("configure"));
    }

    [Theory]
    [InlineData(true, "y", AwsSignInOutcome.SignedIn)]
    [InlineData(false, "n", AwsSignInOutcome.Cancelled)]
    public async Task AnswersTheOverwriteQuestion(bool replace, string expected, AwsSignInOutcome outcome)
    {
        const string question = "\nProfile dsh-bedrock is already configured to use session arn:aws:iam::111122223333:user/bob. Do you want to overwrite it to use arn:aws:iam::111122223333:user/alice instead? (y/n): ";
        // Declining, the real CLI returns without printing "Updated profile …" and exits 0.
        var final = replace ? LoggedIn : new AwsCliResult(0, "", "");
        var runner = new FakeAwsCliRunner().OnInteractive(["login"], SameDeviceOutput(new FakeCliStep.Prompt(question)), final);
        var signIn = new AwsSignIn(new AwsCli(runner), Profiles("[profile dsh-bedrock]\nlogin_session = x\nregion = us-east-1\n"));
        var result = await signIn.LoginAsync("dsh-bedrock", "us-east-1", new AwsSignInOptions { ReplaceExistingSession = replace });
        var (prompt, answer) = Assert.Single(runner.Answers);
        Assert.Equal(question, prompt);
        Assert.Equal(expected, answer);
        Assert.Equal(outcome, result.Outcome);
    }

    [Fact]
    public async Task DeclinesTheAgentToolkitQuestionIfItEverComes()
    {
        var runner = new FakeAwsCliRunner()
            .OnInteractive(["login"],
                SameDeviceOutput(new FakeCliStep.Prompt("\nConfigure AWS skills and the AWS MCP server for your AI coding agent(s)? [y/n/never]: ")), LoggedIn)
            .On(["configure", "set"], "");
        Assert.True((await new AwsSignIn(new AwsCli(runner), Profiles(null)).LoginAsync("dsh-bedrock", "us-east-1")).Succeeded);
        Assert.Equal("n", Assert.Single(runner.Answers).Answer);
    }

    [Fact]
    public async Task RemoteSignInAsksForTheCode()
    {
        var runner = new FakeAwsCliRunner()
            .OnInteractive(["login"],
            [
                new FakeCliStep.Line("Browser will not be automatically opened."),
                new FakeCliStep.Line("Please visit the following URL:"),
                new FakeCliStep.Line(""),
                new FakeCliStep.Line(RemoteUrl),
                new FakeCliStep.Line(""),
                new FakeCliStep.Prompt("\nEnter the authorization code displayed in your browser: "),
            ], LoggedIn)
            .On(["configure", "set"], "");
        AwsSignInLink? asked = null;
        var signIn = new AwsSignIn(new AwsCli(runner), Profiles(null));
        var result = await signIn.LoginAsync("dsh-bedrock", "us-east-1", new AwsSignInOptions
        {
            Remote = true,
            RequestAuthorizationCode = (link, _) =>
            {
                asked = link;
                return Task.FromResult<string?>("  c3RhdGU9eHgmY29kZT15eQ==  \n");
            },
        });
        Assert.True(result.Succeeded);
        Assert.Equal(new Uri(RemoteUrl), asked!.Url);
        Assert.Equal("c3RhdGU9eHgmY29kZT15eQ==", Assert.Single(runner.Answers).Answer);
        Assert.Contains("--remote", Assert.Single(runner.CallsTo("login")).Args);
    }

    [Fact]
    public async Task NoCodeMeansCancelled()
    {
        var runner = new FakeAwsCliRunner().OnInteractive(["login"],
            [new FakeCliStep.Line(RemoteUrl), new FakeCliStep.Prompt("\nEnter the authorization code displayed in your browser: ")], LoggedIn);
        var result = await new AwsSignIn(new AwsCli(runner), Profiles(null)).LoginAsync("dsh-bedrock", "us-east-1", new AwsSignInOptions
        {
            Remote = true,
            RequestAuthorizationCode = (_, _) => Task.FromResult<string?>(null),
        });
        Assert.Equal(AwsSignInOutcome.Cancelled, result.Outcome);
        Assert.Equal(new Uri(RemoteUrl), result.Link!.Url);
    }

    [Fact]
    public async Task CancellingStopsTheCli()
    {
        var runner = new FakeAwsCliRunner().OnInteractive(["login"], SameDeviceOutput(new FakeCliStep.Hang()), LoggedIn);
        using var cts = new CancellationTokenSource();
        var signIn = new AwsSignIn(new AwsCli(runner), Profiles(null));
        var login = signIn.LoginAsync("dsh-bedrock", "us-east-1", new AwsSignInOptions { OnSignInLink = _ => cts.Cancel() }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Empty(runner.CallsTo("configure"));
    }

    [Theory]
    [InlineData("[profile dsh-bedrock]\nsso_session = corp\nsso_account_id = 1\nsso_role_name = Dev\n", AwsSignInOutcome.UseSsoLogin)]
    [InlineData("[profile dsh-bedrock]\nrole_arn = arn:aws:iam::1:role/x\nsource_profile = default\n", AwsSignInOutcome.ProfileHasOtherCredentials)]
    [InlineData("[profile dsh-bedrock]\ncredential_process = vault aws\n", AwsSignInOutcome.ProfileHasOtherCredentials)]
    public async Task ProfilesWithOtherCredentialsAreLeftAlone(string config, AwsSignInOutcome outcome)
    {
        var runner = new FakeAwsCliRunner();
        var result = await new AwsSignIn(new AwsCli(runner), Profiles(config)).LoginAsync("dsh-bedrock", "us-east-1");
        Assert.Equal(outcome, result.Outcome);
        Assert.Contains("dsh-bedrock", result.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task KeysInTheCredentialsFileCountToo()
    {
        var runner = new FakeAwsCliRunner();
        var result = await new AwsSignIn(new AwsCli(runner), Profiles(null, "[work]\naws_access_key_id = AKIA\naws_secret_access_key = s\n"))
            .LoginAsync("work", "us-east-1");
        Assert.Equal(AwsSignInOutcome.ProfileHasOtherCredentials, result.Outcome);
        Assert.Contains("access keys", result.Message);
    }

    [Theory]
    // Real messages from AWS CLI 2.37.5.
    [InlineData(252, "aws: [ERROR]: An error occurred (ParamValidation): argument command: Found invalid choice 'login'", AwsSignInOutcome.CliTooOld)]
    [InlineData(253, "aws: [ERROR]: An error occurred (Configuration): Profile 'keys' is already configured with Access Key credentials.\n\nYou may run 'aws login --profile new-profile-name' to create a new profile with the specified name.", AwsSignInOutcome.ProfileHasOtherCredentials)]
    [InlineData(255, "aws: [ERROR]: Failed to retrieve an auth_code or state from the verification code", AwsSignInOutcome.Failed)]
    [InlineData(255, "aws: [ERROR]: The pending authorization to retrieve an SSO token has expired. The login flow to retrieve an SSO token must be restarted.", AwsSignInOutcome.TimedOut)]
    [InlineData(255, "aws: [ERROR]: Error loading or redeeming a login authorization code: Unable to complete the login process due to an expired authorization code. Please reauthenticate using 'aws login'.", AwsSignInOutcome.TimedOut)]
    [InlineData(255, "aws: [ERROR]: Unable to create or refresh login credentials due to insufficient permissions. You may be missing permission for the 'signin:CreateOAuth2Token' action.", AwsSignInOutcome.NotAllowed)]
    [InlineData(255, "aws: [ERROR]: Could not connect to the endpoint URL: \"https://us-east-1.signin.aws.amazon.com/v1/token\"", AwsSignInOutcome.Failed)]
    [InlineData(130, "", AwsSignInOutcome.Cancelled)]
    public void ExplainsFailures(int exitCode, string stderr, AwsSignInOutcome outcome)
    {
        var result = AwsSignIn.Interpret(new AwsCliResult(exitCode, "", "\n" + stderr + "\n"));
        Assert.Equal(outcome, result.Outcome);
        Assert.DoesNotContain("aws: [ERROR]", result.Message);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AFailedLoginSkipsTheRegion()
    {
        var runner = new FakeAwsCliRunner().OnInteractive(["login"], SameDeviceOutput(),
            new AwsCliResult(255, "", "\naws: [ERROR]: Failed to retrieve an authorization code.\n"));
        var result = await new AwsSignIn(new AwsCli(runner), Profiles(null)).LoginAsync();
        Assert.Equal(AwsSignInOutcome.Failed, result.Outcome);
        Assert.Contains("Failed to retrieve an authorization code.", result.Message);
        Assert.Equal(new Uri(SameDeviceUrl), result.Link!.Url);
        Assert.Empty(runner.CallsTo("configure"));
        Assert.Equal(["login", "--profile", "dsh-bedrock", "--region", "us-east-1"], runner.Calls[0].Args);
    }

    [Theory]
    [InlineData(SameDeviceUrl, true)]
    [InlineData("  https://device.sso.us-east-1.amazonaws.com/  ", true)]
    [InlineData("Please visit the following URL:", false)]
    [InlineData("open https://example.com now", false)]
    [InlineData("http://127.0.0.1:53123/oauth/callback", false)]
    [InlineData("", false)]
    public void FindsTheSignInUrl(string line, bool found) => Assert.Equal(found, AwsSignIn.ExtractSignInUrl(line) is not null);

    [Fact]
    public async Task SsoSignInShowsTheDeviceCode()
    {
        var runner = new FakeAwsCliRunner().OnInteractive(["sso", "login"],
        [
            new FakeCliStep.Line("Browser will not be automatically opened."),
            new FakeCliStep.Line("Please visit the following URL:"),
            new FakeCliStep.Line(""),
            new FakeCliStep.Line("https://device.sso.us-east-1.amazonaws.com/"),
            new FakeCliStep.Line(""),
            new FakeCliStep.Line("Then enter the code:"),
            new FakeCliStep.Line(""),
            new FakeCliStep.Line("WDXB-PQRT"),
        ], new AwsCliResult(0, "Successfully logged into Start URL: https://corp.awsapps.com/start\n", ""));
        var links = new List<AwsSignInLink>();
        var signIn = new AwsSignIn(new AwsCli(runner), Profiles("[profile corp-dev]\nsso_session = corp\nsso_account_id = 1\nsso_role_name = Dev\n"));
        var result = await signIn.SsoLoginAsync("corp-dev", new AwsSignInOptions { Remote = true, OnSignInLink = links.Add });
        Assert.True(result.Succeeded);
        Assert.Equal("WDXB-PQRT", result.Link!.UserCode);
        Assert.Equal(new Uri("https://device.sso.us-east-1.amazonaws.com/"), result.Link.Url);
        Assert.Equal(2, links.Count);
        Assert.Equal(["sso", "login", "--profile", "corp-dev", "--use-device-code", "--no-browser"], runner.Calls[0].Args);
    }

    [Fact]
    public async Task SsoSignInNeedsAnSsoProfile()
    {
        var runner = new FakeAwsCliRunner();
        var result = await new AwsSignIn(new AwsCli(runner), Profiles("[profile dsh-bedrock]\nlogin_session = x\n")).SsoLoginAsync("dsh-bedrock");
        Assert.Equal(AwsSignInOutcome.NotSsoProfile, result.Outcome);
        Assert.Empty(runner.Calls);
    }

    // MARK: - Who is signed in

    [Fact]
    public async Task VerifiesWhoIsSignedIn()
    {
        var runner = new FakeAwsCliRunner().On(["sts", "get-caller-identity"],
            """
            {
                "UserId": "AIDASAMPLEUSERID",
                "Account": "123456789012",
                "Arn": "arn:aws:iam::123456789012:user/alice"
            }
            """);
        var identity = await new AwsSignIn(new AwsCli(runner), Profiles(null)).VerifyAsync("dsh-bedrock", "eu-central-1");
        Assert.Equal("123456789012", identity.Account);
        Assert.Equal("AIDASAMPLEUSERID", identity.UserId);
        Assert.Equal("1234-5678-9012", identity.DisplayAccount);
        Assert.Equal("Signed in to AWS account 1234-5678-9012 as alice.", identity.Description);
        Assert.Equal(["sts", "get-caller-identity", "--profile", "dsh-bedrock", "--region", "eu-central-1", "--output", "json"], runner.Calls[0].Args);
    }

    [Theory]
    [InlineData("arn:aws:sts::123456789012:assumed-role/Developer/alice@example.com", "alice@example.com (role Developer)")]
    [InlineData("arn:aws:iam::123456789012:root", "the account's root user")]
    [InlineData("arn:aws:iam::123456789012:user/team/bob", "bob")]
    public void NamesThePrincipal(string arn, string name) => Assert.Equal(name, new CallerIdentity("123456789012", arn, "X").PrincipalName);

    [Fact]
    public async Task VerifyingWithoutASignInAsksForOne()
    {
        var runner = new FakeAwsCliRunner().Fail(["sts"],
            "aws: [ERROR]: Unable to retrieve credentials: Error loading login session token: Unable to load a existing login session for session arn:aws:iam::1:user/a, Please reauthenticate with 'aws login'.", 253);
        var ex = await Assert.ThrowsAsync<AwsSignInRequiredException>(() => new AwsSignIn(new AwsCli(runner), Profiles(null)).VerifyAsync("dsh-bedrock"));
        Assert.Equal("dsh-bedrock", ex.Profile);
    }
}
