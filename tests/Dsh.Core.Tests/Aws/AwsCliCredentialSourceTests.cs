namespace Dsh.Core.Tests;

public sealed class AwsCliCredentialSourceTests
{
    private static readonly DateTimeOffset Now = ManualClock.Epoch;

    /// <summary>`aws configure export-credentials --format process` output (the shape AWS CLI 2.37.5 prints).</summary>
    private static string Process(string keyId, DateTimeOffset? expires, string? token = "IQoJb3JpZ2luX2VjEXAMPLE") =>
        "{\n  \"Version\": 1,\n  \"AccessKeyId\": \"" + keyId + "\",\n  \"SecretAccessKey\": \"wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY\"" +
        (token is null ? "" : ",\n  \"SessionToken\": \"" + token + "\"") +
        (expires is { } e ? ",\n  \"Expiration\": \"" + e.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", System.Globalization.CultureInfo.InvariantCulture) + "\"" : "") + "\n}\n";

    [Fact]
    public void ParsesWhatTheCliPrints()
    {
        // Verbatim from a real CLI (credential_process profile).
        var real = AwsCliCredentialSource.ParseProcessOutput("""
            {
              "Version": 1,
              "AccessKeyId": "ASIAPROC",
              "SecretAccessKey": "sec",
              "SessionToken": "tok",
              "Expiration": "2026-09-29T19:55:23.728587+00:00"
            }
            """);
        Assert.Equal("ASIAPROC", real.AccessKeyId);
        Assert.Equal("sec", real.SecretAccessKey);
        Assert.Equal("tok", real.SessionToken);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 19, 55, 23, TimeSpan.Zero).AddTicks(7285870), real.Expiration);

        var keys = AwsCliCredentialSource.ParseProcessOutput("""{"Version": 1, "AccessKeyId": "AKIAEXAMPLE", "SecretAccessKey": "secretexample"}""");
        Assert.Null(keys.SessionToken);
        Assert.Null(keys.Expiration);

        var zulu = AwsCliCredentialSource.ParseProcessOutput("""{"AccessKeyId":"A","SecretAccessKey":"S","Expiration":"2026-09-29T20:00:00Z"}""");
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 20, 0, 0, TimeSpan.Zero), zulu.Expiration);
    }

    [Fact]
    public void UnusableOutputIsReported()
    {
        Assert.Equal("dsh-bedrock", Assert.Throws<AwsSignInRequiredException>(() => AwsCliCredentialSource.ParseProcessOutput("{}", "dsh-bedrock")).Profile);
        Assert.Throws<AwsSignInRequiredException>(() => AwsCliCredentialSource.ParseProcessOutput("not json"));
        Assert.Throws<AwsCliException>(() => AwsCliCredentialSource.ParseProcessOutput("""{"AccessKeyId":"A","SecretAccessKey":"S","Expiration":"soon"}"""));
    }

    [Fact]
    public async Task RunsExportCredentialsForTheProfile()
    {
        var runner = new FakeAwsCliRunner().On(["configure", "export-credentials"], Process("ASIA1", Now.AddHours(1)));
        var source = new AwsCliCredentialSource(runner, "dsh-bedrock", time: new ManualClock());
        var credentials = await source.GetAsync();
        Assert.Equal("ASIA1", credentials.AccessKeyId);
        Assert.Equal(["configure", "export-credentials", "--format", "process", "--profile", "dsh-bedrock"], runner.Calls[0].Args);
        Assert.Equal(TimeSpan.FromMinutes(2), runner.Calls[0].Options!.Timeout);
        Assert.Equal(["configure", "export-credentials", "--format", "process", "--profile", "p", "--region", "eu-central-1"],
            new AwsCliCredentialSource(runner, "p", "eu-central-1").ExportArguments);
    }

    [Fact]
    public async Task CachesUntilFiveMinutesBeforeExpiry()
    {
        var clock = new ManualClock();
        var runner = new FakeAwsCliRunner().OnSequence(["configure", "export-credentials"],
            new AwsCliResult(0, Process("ASIA1", Now.AddMinutes(60)), ""),
            new AwsCliResult(0, Process("ASIA2", Now.AddMinutes(120)), ""));
        var source = new AwsCliCredentialSource(runner, "dsh-bedrock", time: clock);

        Assert.Equal("ASIA1", (await source.GetAsync()).AccessKeyId);
        clock.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal("ASIA1", (await source.GetAsync()).AccessKeyId); // 6 minutes left: still cached
        Assert.Single(runner.Calls);
        clock.Advance(TimeSpan.FromMinutes(1.5));
        Assert.Equal("ASIA2", (await source.GetAsync()).AccessKeyId); // 4.5 minutes left: fetched again
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task ForceRefreshAndInvalidateFetchAgain()
    {
        var runner = new FakeAwsCliRunner().OnSequence(["configure", "export-credentials"],
            new AwsCliResult(0, Process("ASIA1", Now.AddHours(1)), ""),
            new AwsCliResult(0, Process("ASIA2", Now.AddHours(1)), ""),
            new AwsCliResult(0, Process("ASIA3", Now.AddHours(1)), ""));
        var source = new AwsCliCredentialSource(runner, "dsh-bedrock", time: new ManualClock());
        Assert.Equal("ASIA1", (await source.GetAsync()).AccessKeyId);
        Assert.Equal("ASIA2", (await source.GetAsync(forceRefresh: true)).AccessKeyId);
        Assert.Equal("ASIA2", (await source.GetAsync()).AccessKeyId);
        source.Invalidate();
        Assert.Equal("ASIA3", (await source.GetAsync()).AccessKeyId);
    }

    [Fact]
    public async Task LongTermKeysStayCached()
    {
        var clock = new ManualClock();
        var runner = new FakeAwsCliRunner().On(["configure", "export-credentials"], Process("AKIA", null, token: null));
        var source = new AwsCliCredentialSource(runner, "keys", time: clock);
        await source.GetAsync();
        clock.Advance(TimeSpan.FromDays(30));
        await source.GetAsync();
        Assert.Single(runner.Calls);
    }

    [Theory]
    // Real CLI messages for an `aws login` session that is gone, an SSO token that expired, and a missing profile.
    [InlineData("aws: [ERROR]: Unable to retrieve credentials: Error loading login session token: Unable to load a existing login session for session arn:aws:iam::111122223333:user/alice, Please reauthenticate with 'aws login'.", 253)]
    [InlineData("aws: [ERROR]: Unable to retrieve credentials: Your session has expired. Please reauthenticate using 'aws login'.", 253)]
    [InlineData("aws: [ERROR]: Error loading SSO Token: Token for corp does not exist", 255)]
    [InlineData("aws: [ERROR]: The SSO session associated with this profile has expired or is otherwise invalid. To refresh this SSO session run aws sso login with the corresponding profile.", 255)]
    [InlineData("aws: [ERROR]: Unable to retrieve credentials: The config profile (dsh-bedrock) could not be found", 253)]
    public async Task ExpiredSignInsAskToSignInAgain(string stderr, int exitCode)
    {
        var runner = new FakeAwsCliRunner().Fail(["configure", "export-credentials"], stderr, exitCode);
        var source = new AwsCliCredentialSource(runner, "dsh-bedrock");
        var ex = await Assert.ThrowsAsync<AwsSignInRequiredException>(() => source.GetAsync());
        Assert.Equal("dsh-bedrock", ex.Profile);
        Assert.DoesNotContain("aws: [ERROR]", ex.Message);
    }

    [Fact]
    public async Task OtherFailuresAreNotSignInProblemsAndArentCached()
    {
        var runner = new FakeAwsCliRunner().OnSequence(["configure", "export-credentials"],
            new AwsCliResult(255, "", "\naws: [ERROR]: Could not connect to the endpoint URL: \"https://us-east-1.signin.aws.amazon.com/v1/token\"\n"),
            new AwsCliResult(0, Process("ASIA1", Now.AddHours(1)), ""));
        var source = new AwsCliCredentialSource(runner, "dsh-bedrock", time: new ManualClock());
        var ex = await Assert.ThrowsAsync<AwsCliException>(() => source.GetAsync());
        Assert.Equal(AwsCliFailure.Network, ex.Kind);
        Assert.Equal("ASIA1", (await source.GetAsync()).AccessKeyId);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneExport()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeAwsCliRunner().On(["configure", "export-credentials"], async (_, _) =>
        {
            await release.Task;
            return new AwsCliResult(0, Process("ASIA1", Now.AddHours(1)), "");
        });
        var source = new AwsCliCredentialSource(runner, "dsh-bedrock", time: new ManualClock());
        var callers = Enumerable.Range(0, 8).Select(i => source.GetAsync(forceRefresh: i % 2 == 0)).ToList();
        using var impatient = new CancellationTokenSource();
        var cancelled = source.GetAsync(cancellationToken: impatient.Token);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        release.SetResult();
        var results = await Task.WhenAll(callers);
        Assert.All(results, c => Assert.Equal("ASIA1", c.AccessKeyId));
        Assert.Single(runner.Calls);
        Assert.Equal("ASIA1", (await source.GetAsync()).AccessKeyId);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task ItIsTheBedrockClientsCredentialSource()
    {
        IAwsCredentialSource source = new AwsCliCredentialSource(new FakeAwsCliRunner()
            .On(["configure", "export-credentials"], Process("ASIA1", Now.AddHours(1))), "dsh-bedrock", time: new ManualClock());
        var credentials = await source.GetAsync();
        Assert.DoesNotContain("wJalr", credentials.ToString());
    }
}
