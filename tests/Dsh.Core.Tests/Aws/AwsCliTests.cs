using System.Diagnostics;

namespace Dsh.Core.Tests;

public sealed class AwsCliTests
{
    // MARK: - Versions

    [Theory]
    [InlineData("aws-cli/2.33.4 Python/3.13.9 Windows/11 exe/AMD64", "2.33.4")]
    [InlineData("aws-cli/2.37.5 Python/3.11.15 Linux/6.18.44-fc-v49 source/x86_64.ubuntu.24\n", "2.37.5")] // real output
    [InlineData("aws-cli/2.32.0 Python/3.13.7 Windows/10 exe/AMD64 prompt/off", "2.32.0")]
    [InlineData("aws-cli/1.29.62 Python/3.11.4 Windows/10 botocore/1.31.62", "1.29.62")]
    [InlineData("\n\naws-cli/2.31.39 Python/3.13.7 Windows/11 exe/AMD64\n", "2.31.39")]
    public void ReadsTheVersion(string printed, string expected) => Assert.Equal(Version.Parse(expected), AwsCli.ParseVersion(printed));

    [Theory]
    [InlineData("")]
    [InlineData("'aws' is not recognized as an internal or external command")]
    [InlineData("aws-cli/two")]
    public void NoVersionInGarbage(string printed) => Assert.Null(AwsCli.ParseVersion(printed));

    [Fact]
    public void AwsLoginNeeds2_32() => Assert.Equal(new Version(2, 32, 0), AwsCli.MinimumVersion);

    // MARK: - Locating

    /// <summary>A rooted path in this OS's form ("/usr/bin/aws" stays; on Windows it gains a drive).</summary>
    private static string P(string path) => Path.GetFullPath(path);

    private static AwsCliSearch Search(IEnumerable<string> existing, string? explicitPath = null) => new()
    {
        ExplicitPath = explicitPath,
        PathDirectories = ["/usr/bin", "/home/me/.local/bin", "/usr/bin/"],
        KnownLocations = ["/pf/Amazon/AWSCLIV2/aws", "/home/me/AppData/Local/Programs/Amazon/AWSCLIV2/aws"],
        FileName = "aws",
        FileExists = existing.Select(P).ToHashSet().Contains,
        IgnoreCase = false,
    };

    [Fact]
    public void CandidatesComeExplicitThenPathThenInstallFolders()
    {
        var search = Search(["/opt/custom/aws", "/home/me/.local/bin/aws", "/pf/Amazon/AWSCLIV2/aws", "/usr/bin/aws"], "/opt/custom/aws");
        Assert.Equal(new[] { "/opt/custom/aws", "/usr/bin/aws", "/home/me/.local/bin/aws", "/pf/Amazon/AWSCLIV2/aws" }.Select(P), AwsCli.Candidates(search));
        Assert.Equal(P("/opt/custom/aws"), AwsCli.Locate(search));
    }

    [Fact]
    public void MissingFilesAndDuplicatesAreSkipped()
    {
        var search = Search(["/pf/Amazon/AWSCLIV2/aws"], "/nowhere/aws");
        Assert.Equal([P("/pf/Amazon/AWSCLIV2/aws")], AwsCli.Candidates(search));
        Assert.Null(AwsCli.Locate(Search([])));
    }

    [Fact]
    public void WindowsPathsCompareWithoutCase()
    {
        var search = Search(["/usr/bin/aws", "/USR/BIN/aws"]) with { PathDirectories = ["/usr/bin", "/USR/BIN"], IgnoreCase = true };
        Assert.Single(AwsCli.Candidates(search));
    }

    private static Func<string, IAwsCliRunner> Versions(Dictionary<string, string> printedByPath) => path =>
        new FakeAwsCliRunner().On(["--version"], printedByPath.Single(p => P(p.Key) == path).Value);

    [Fact]
    public async Task MissingWhenNothingIsInstalled()
    {
        var status = await AwsCli.CheckAsync(Search([]), _ => throw new InvalidOperationException("nothing to run"));
        Assert.IsType<AwsCliStatus.Missing>(status);
        Assert.Contains("isn't installed", status.Summary);
    }

    [Fact]
    public async Task ReadyWhenNewEnough()
    {
        var status = await AwsCli.CheckAsync(Search(["/pf/Amazon/AWSCLIV2/aws"]),
            Versions(new() { ["/pf/Amazon/AWSCLIV2/aws"] = "aws-cli/2.33.4 Python/3.13.9 Windows/11 exe/AMD64" }));
        var ready = Assert.IsType<AwsCliStatus.Ready>(status);
        Assert.Equal(new Version(2, 33, 4), ready.Version);
        Assert.Equal(P("/pf/Amazon/AWSCLIV2/aws"), ready.Path);
        Assert.Equal("AWS CLI 2.33.4 is ready.", ready.Summary);
    }

    [Fact]
    public async Task AnOldCliOnPathDoesntHideANewOneInTheInstallFolder()
    {
        var status = await AwsCli.CheckAsync(Search(["/usr/bin/aws", "/pf/Amazon/AWSCLIV2/aws"]), Versions(new()
        {
            ["/usr/bin/aws"] = "aws-cli/1.29.62 Python/3.11.4 Linux/6 botocore/1.31.62",
            ["/pf/Amazon/AWSCLIV2/aws"] = "aws-cli/2.37.5 Python/3.13.9 Windows/11 exe/AMD64",
        }));
        Assert.Equal(P("/pf/Amazon/AWSCLIV2/aws"), Assert.IsType<AwsCliStatus.Ready>(status).Path);
    }

    [Fact]
    public async Task TooOldReportsTheNewestOldOne()
    {
        var status = await AwsCli.CheckAsync(Search(["/usr/bin/aws", "/home/me/.local/bin/aws", "/pf/Amazon/AWSCLIV2/aws"]), Versions(new()
        {
            ["/usr/bin/aws"] = "aws-cli/1.29.62 Python/3.11.4 Linux/6 botocore/1.31.62",
            ["/home/me/.local/bin/aws"] = "",
            ["/pf/Amazon/AWSCLIV2/aws"] = "aws-cli/2.31.39 Python/3.13.7 Windows/11 exe/AMD64",
        }));
        var old = Assert.IsType<AwsCliStatus.TooOld>(status);
        Assert.Equal(new Version(2, 31, 39), old.Version);
        Assert.Contains("2.32.0 or newer", old.Summary);
        Assert.Contains("needs version 2", new AwsCliStatus.TooOld(new Version(1, 29, 62), "/usr/bin/aws").Summary);
    }

    [Fact]
    public async Task BrokenWhenItDoesntSayItsVersion()
    {
        var status = await AwsCli.CheckAsync(Search(["/usr/bin/aws"]),
            _ => new FakeAwsCliRunner().On(["--version"], "", "Fatal Python error: init_fs_encoding", exitCode: 1));
        var broken = Assert.IsType<AwsCliStatus.Broken>(status);
        Assert.Contains("Fatal Python error", broken.Problem);
    }

    // MARK: - Arguments and JSON

    [Fact]
    public void ProfileRegionAndJsonAreAppended()
    {
        Assert.Equal(["bedrock", "list-foundation-models", "--profile", "dsh-bedrock", "--region", "eu-central-1", "--output", "json"],
            AwsCli.Arguments(["bedrock", "list-foundation-models"], "dsh-bedrock", "eu-central-1"));
        Assert.Equal(["sts", "get-caller-identity"], AwsCli.Arguments(["sts", "get-caller-identity"], null, "", json: false));
    }

    [Fact]
    public async Task RunJsonParsesOutputAndMapsFailures()
    {
        var runner = new FakeAwsCliRunner()
            .On(["sts"], """{"UserId":"AIDAEXAMPLE","Account":"123456789012","Arn":"arn:aws:iam::123456789012:user/alice"}""")
            .On(["bedrock", "put-use-case-for-model-access"], "")
            .On(["bedrock", "weird"], "not json")
            .Fail(["bedrock", "list-foundation-models"], "aws: [ERROR]: Unable to retrieve credentials: Your session has expired. Please reauthenticate using 'aws login'.", 253);
        var cli = new AwsCli(runner);

        var identity = await cli.RunJsonAsync(["sts", "get-caller-identity"], "p", "us-east-1");
        Assert.Equal("123456789012", identity["Account"]!.GetValue<string>());
        Assert.Empty((await cli.RunJsonAsync(["bedrock", "put-use-case-for-model-access"], "p", "us-east-1")).AsObject());
        var bad = await Assert.ThrowsAsync<AwsCliException>(() => cli.RunJsonAsync(["bedrock", "weird"], "p", "us-east-1"));
        Assert.Contains("couldn't read", bad.Message);
        var signIn = await Assert.ThrowsAsync<AwsSignInRequiredException>(() => cli.RunJsonAsync(["bedrock", "list-foundation-models"], "dsh-bedrock", "us-east-1"));
        Assert.Equal("dsh-bedrock", signIn.Profile);
        Assert.Contains("expired", signIn.Message);
        Assert.Equal(TimeSpan.FromMinutes(2), runner.Calls[0].Options!.Timeout);
    }

    // MARK: - Errors

    [Theory]
    // Texts as the CLI prints them (awscli errorhandler + botocore exceptions); several captured from a real CLI.
    [InlineData("Unable to retrieve credentials: Error loading login session token: Unable to load a existing login session for session arn:aws:iam::111122223333:user/alice, Please reauthenticate with 'aws login'.", "expired")]
    [InlineData("Unable to retrieve credentials: Your session has expired. Please reauthenticate using 'aws login'.", "expired")]
    [InlineData("Unable to refresh login credentials because of a change in your password. Please reauthenticate with your new password using 'aws login'.", "password")]
    [InlineData("Error loading SSO Token: Token for corp does not exist", "SSO")]
    [InlineData("The SSO session associated with this profile has expired or is otherwise invalid. To refresh this SSO session run aws sso login with the corresponding profile.", "SSO")]
    [InlineData("Error when retrieving token from sso: Token has expired and refresh failed", "SSO")]
    [InlineData("Unable to retrieve credentials: The config profile (nope) could not be found", "isn't signed in")]
    [InlineData("Unable to locate credentials. You can configure credentials by running \"aws login\".", "isn't signed in")]
    [InlineData("An error occurred (ExpiredTokenException) when calling the ListFoundationModels operation: The security token included in the request is expired", "expired")]
    [InlineData("An error occurred (UnrecognizedClientException) when calling the ListFoundationModels operation: The security token included in the request is invalid.", "didn't accept")]
    public void SignInProblemsAskToSignInAgain(string error, string mentions)
    {
        var classified = AwsCliErrors.Classify(new AwsCliResult(255, "", "\naws: [ERROR]: " + error + "\n"), "bedrock");
        Assert.Equal(AwsCliFailure.SignInRequired, classified.Kind);
        Assert.Contains(mentions, classified.Message);
        var exception = Assert.IsType<AwsSignInRequiredException>(AwsCliErrors.ToException(classified, "dsh-bedrock"));
        Assert.Equal("dsh-bedrock", exception.Profile);
        Assert.IsType<AwsCliException>(exception.InnerException);
    }

    [Fact]
    public void AccessDeniedNamesThePermission()
    {
        // Captured from a real CLI talking to a stub endpoint that denied the call.
        const string stderr = "\naws: [ERROR]: An error occurred (AccessDeniedException) when calling the GetFoundationModelAvailability operation: User: arn:aws:sts::111122223333:assumed-role/Dev/alice is not authorized to perform: bedrock:GetFoundationModelAvailability because no identity-based policy allows the bedrock:GetFoundationModelAvailability action\n";
        var error = AwsCliErrors.Classify(new AwsCliResult(254, "", stderr), "bedrock");
        Assert.Equal(AwsCliFailure.AccessDenied, error.Kind);
        Assert.Equal("AccessDeniedException", error.Code);
        Assert.Equal("GetFoundationModelAvailability", error.Operation);
        Assert.Equal("bedrock:GetFoundationModelAvailability", error.Permission);
        Assert.Contains("don't allow bedrock:GetFoundationModelAvailability", error.Message);
        Assert.Equal(stderr.Trim(), error.Details);
        Assert.IsType<AwsCliException>(AwsCliErrors.ToException(error, "p"));
    }

    [Fact]
    public void AccessDeniedWithoutAPermissionInTheMessageUsesTheOperation()
    {
        var error = AwsCliErrors.Classify(new AwsCliResult(254, "",
            "An error occurred (AccessDeniedException) when calling the ListInferenceProfiles operation (reached max retries: 2): Access denied."), "bedrock");
        Assert.Equal("bedrock:ListInferenceProfiles", error.Permission);
        var explicitDeny = AwsCliErrors.Classify(new AwsCliResult(254, "",
            "An error occurred (AccessDeniedException) when calling the InvokeModel operation: User: arn:aws:iam::1:user/a is not authorized to perform: bedrock:InvokeModel on resource: x with an explicit deny in a service control policy"), "bedrock");
        Assert.Contains("explicitly blocks bedrock:InvokeModel", explicitDeny.Message);
    }

    [Fact]
    public void SignInWithoutTheCliPermissionIsAccessDenied()
    {
        var error = AwsCliErrors.Classify(new AwsCliResult(255, "",
            "aws: [ERROR]: Unable to create or refresh login credentials due to insufficient permissions. You may be missing permission for the 'signin:CreateOAuth2Token' action."));
        Assert.Equal(AwsCliFailure.AccessDenied, error.Kind);
        Assert.Equal("signin:CreateOAuth2Token", error.Permission);
        Assert.Contains(BedrockPermissions.SignInManagedPolicy, error.Message);
    }

    [Theory]
    [InlineData("aws: [ERROR]: An error occurred (ParamValidation): argument command: Found invalid choice 'login'\n\nusage: aws [options] <command> <subcommand> [<subcommand> ...] [parameters]", AwsCliFailure.NotSupported)]
    [InlineData("aws: error: argument command: Invalid choice, valid choices are:", AwsCliFailure.NotSupported)]
    [InlineData("Could not connect to the endpoint URL: \"https://bedrock.us-east-1.amazonaws.com/foundation-models\"", AwsCliFailure.Network)]
    [InlineData("SSL validation failed for https://bedrock.us-east-1.amazonaws.com/ [SSL: CERTIFICATE_VERIFY_FAILED]", AwsCliFailure.Network)]
    [InlineData("An error occurred (ThrottlingException) when calling the ListFoundationModels operation (reached max retries: 2): Rate exceeded", AwsCliFailure.Throttled)]
    [InlineData("An error occurred (ResourceNotFoundException) when calling the GetUseCaseForModelAccess operation: Use case form not found.", AwsCliFailure.NotFound)]
    [InlineData("An error occurred (ConflictException) when calling the CreateFoundationModelAgreement operation: Agreement is being processed.", AwsCliFailure.Conflict)]
    [InlineData("An error occurred (ValidationException) when calling the GetFoundationModelAvailability operation: Model id is invalid.", AwsCliFailure.Invalid)]
    [InlineData("You must specify a region. You can also configure your region by running \"aws configure\".", AwsCliFailure.Invalid)]
    [InlineData("Something nobody expected happened.", AwsCliFailure.Other)]
    public void OtherFailures(string stderr, AwsCliFailure kind) =>
        Assert.Equal(kind, AwsCliErrors.Classify(new AwsCliResult(254, "", stderr), "bedrock").Kind);

    [Fact]
    public void TimeoutsAndMessagesAreReadable()
    {
        Assert.Equal(AwsCliFailure.TimedOut, AwsCliErrors.Classify(new AwsCliResult(-1, "", "", TimedOut: true)).Kind);
        var other = AwsCliErrors.Classify(new AwsCliResult(255, "", "\naws: [ERROR]: Something nobody expected happened.\n"));
        Assert.Equal("Something nobody expected happened.", other.Message);
        Assert.Equal("Use case form not found.", AwsCliErrors.Classify(new AwsCliResult(254, "",
            "An error occurred (ResourceNotFoundException) when calling the GetUseCaseForModelAccess operation: Use case form not found.")).Message);
        Assert.Equal("argument command: Found invalid choice 'logn'", AwsCliErrors.CleanMessage(
            "\naws: [ERROR]: An error occurred (ParamValidation): argument command: Found invalid choice 'logn'\n\nusage: aws [options] <command>\nTo see help text, you can run:\n"));
        Assert.Equal("The AWS CLI stopped with an error.", AwsCliErrors.CleanMessage(""));
    }

    // MARK: - The real runner, against a stand-in "aws" script

    private static string Script(TempDirectory dir, string body)
    {
        var path = dir.Write("aws", "#!/bin/bash\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [UnixFact]
    public async Task PassesArgumentsVerbatimAndSetsTheEnvironment()
    {
        using var dir = new TempDirectory("dsh-aws");
        var aws = Script(dir, """
            for a in "$@"; do echo "arg=[$a]"; done
            echo "pager=[${AWS_PAGER-unset}] hint=[$AWS_CLI_AGENT_TOOLKIT_HINT_DISABLED] prompt=[$AWS_CLI_AUTO_PROMPT] enc=[$AWS_CLI_OUTPUT_ENCODING] fmt=[$AWS_CLI_ERROR_FORMAT]"
            echo "extra=[$DSH_EXTRA] gone=[${HOME-unset}]"
            echo "café ✓" >&2
            exit 3
            """);
        var lines = new List<AwsCliLine>();
        var result = await new ProcessAwsCliRunner(aws).RunAsync(["--form-data", "a b \"c\" 'd' $HOME;x"], new AwsCliRunOptions
        {
            Environment = new Dictionary<string, string?> { ["DSH_EXTRA"] = "yes", ["HOME"] = null },
            OnLine = line => { lock (lines) lines.Add(line); },
        });
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("arg=[--form-data]\narg=[a b \"c\" 'd' $HOME;x]\n", result.Stdout);
        Assert.Contains("pager=[] hint=[true] prompt=[off] enc=[UTF-8] fmt=[legacy]", result.Stdout);
        Assert.Contains("extra=[yes] gone=[unset]", result.Stdout);
        Assert.Equal("café ✓\n", result.Stderr);
        lock (lines) Assert.Contains(new AwsCliLine("café ✓", true), lines);
    }

    [UnixFact]
    public async Task AnswersPromptsThatHaveNoNewline()
    {
        using var dir = new TempDirectory("dsh-aws");
        var aws = Script(dir, """
            echo "Attempting to open your default browser."
            printf 'Do you want to overwrite it to use B instead? (y/n): '
            read answer
            echo "got [$answer]"
            """);
        var prompts = new List<string>();
        var result = await new ProcessAwsCliRunner(aws).RunAsync([], new AwsCliRunOptions
        {
            AnswerPrompt = (text, _) =>
            {
                lock (prompts) prompts.Add(text);
                return Task.FromResult(text.EndsWith("(y/n): ", StringComparison.Ordinal) ? "y" : null);
            },
            Timeout = TimeSpan.FromSeconds(30),
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("got [y]", result.Stdout);
        lock (prompts) Assert.Contains("Do you want to overwrite it to use B instead? (y/n): ", prompts);
    }

    [UnixFact]
    public async Task StandardInputIsClosedUnlessSomethingAnswers()
    {
        using var dir = new TempDirectory("dsh-aws");
        var aws = Script(dir, "if read line; then echo \"in [$line]\"; else echo eof; fi");
        Assert.Equal("eof\n", (await new ProcessAwsCliRunner(aws).RunAsync([])).Stdout);
        Assert.Equal("in [hello]\n", (await new ProcessAwsCliRunner(aws).RunAsync([], new AwsCliRunOptions { StandardInput = "hello\n" })).Stdout);
    }

    [UnixFact]
    public async Task TimesOutAndCancels()
    {
        using var dir = new TempDirectory("dsh-aws");
        var aws = Script(dir, "echo started; sleep 30");
        var clock = Stopwatch.StartNew();
        var timedOut = await new ProcessAwsCliRunner(aws).RunAsync([], new AwsCliRunOptions { Timeout = TimeSpan.FromMilliseconds(500) });
        Assert.True(timedOut.TimedOut);
        Assert.Equal(-1, timedOut.ExitCode);
        Assert.Equal(AwsCliFailure.TimedOut, AwsCliErrors.Classify(timedOut).Kind);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessAwsCliRunner(aws).RunAsync([], null, cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
    }

    [UnixFact]
    public async Task AFailingAnswererStopsTheCli()
    {
        using var dir = new TempDirectory("dsh-aws");
        var aws = Script(dir, "printf 'Enter the authorization code displayed in your browser: '; read code; echo done");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProcessAwsCliRunner(aws).RunAsync([], new AwsCliRunOptions
        {
            AnswerPrompt = (_, _) => throw new InvalidOperationException("the window closed"),
            Timeout = TimeSpan.FromSeconds(30),
        }));
    }

    [UnixFact]
    public async Task ChecksARealExecutable()
    {
        using var dir = new TempDirectory("dsh-aws");
        var aws = Script(dir, "echo 'aws-cli/2.37.5 Python/3.13.9 Linux/6.8 exe/x86_64.ubuntu.24'");
        var status = await AwsCli.CheckAsync(new AwsCliSearch { ExplicitPath = aws, FileName = "aws", IgnoreCase = false });
        Assert.Equal(new Version(2, 37, 5), Assert.IsType<AwsCliStatus.Ready>(status).Version);
        Assert.Equal(new Version(2, 37, 5), await AwsCli.ForPath(aws).VersionAsync());
    }

    [Fact]
    public async Task AMissingExecutableIsAnIOException() =>
        await Assert.ThrowsAsync<IOException>(() => new ProcessAwsCliRunner("/nonexistent/dsh/aws").RunAsync(["--version"]));
}
