namespace Dsh.Core.Tests;

public sealed class FailoverClientTests
{
    private static readonly ProviderProfile Spark = new(ProviderKind.OpenAICompat, "Spark", "http://192.168.1.42:8000/v1", "qwen");
    private static readonly ProviderProfile Kimi = new(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.ai/coding/v1", "kimi-for-coding");
    private static readonly ProviderProfile Halo = new(ProviderKind.OpenAICompat, "Strix Halo", "http://evo-x2:8000/v1", "halo27b") { Backup = true };

    private static readonly LlmRequest Request = new("sys", [LlmMessage.User("hi")], [], "qwen", Temperature: 0.2, MaxTokens: 100);

    private static FakeProviderClient Down(ProviderProfile profile, Exception? error = null) =>
        new(profile, (_, _) => FakeProviderClient.Fails(error ?? LlmException.Connection("connection refused")));

    private static FakeProviderClient Up(ProviderProfile profile, string reply) =>
        new(profile, (_, _) => FakeProviderClient.Text(reply));

    private static async Task<string> Text(IProviderClient client)
    {
        var text = "";
        await foreach (var ev in client.StreamAsync(Request))
            if (ev is LlmStreamEvent.Text t) text += t.Delta;
        return text;
    }

    [Fact]
    public async Task APrimaryThatDoesntAnswerHandsTheTurnToTheFirstHealthyBackup()
    {
        var notices = new List<FailoverNotice>();
        var broken = Down(Halo);
        var kimi = Up(Kimi, "from kimi");
        var client = new FailoverClient(Down(Spark), [broken, kimi], notices.Add);

        Assert.Equal("from kimi", await Text(client));
        // The backup got the same conversation with its own model and settings.
        var sent = kimi.Requests.Single();
        Assert.Equal("kimi-for-coding", sent.Model);
        Assert.Null(sent.Temperature);
        Assert.Null(sent.MaxTokens);
        Assert.Equal(Request.Messages, sent.Messages);
        Assert.Single(broken.Requests); // it was tried first
        var notice = Assert.Single(notices);
        Assert.Equal("Spark", notice.Primary);
        Assert.Equal("Kimi Code", notice.Backup);
        Assert.StartsWith("Spark didn't answer (server unreachable", notice.Text);
        Assert.EndsWith("so this turn ran on Kimi Code.", notice.Text);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task RefusalsAnotherServerMayNotHaveFailOver(int status)
    {
        var client = new FailoverClient(Down(Spark, LlmException.Http(status, "nope")), [Up(Kimi, "ok")]);
        Assert.Equal("ok", await Text(client));
    }

    [Fact]
    public async Task ARequestTheServerCalledWrongIsNotMoved()
    {
        var kimi = Up(Kimi, "ok");
        var client = new FailoverClient(Down(Spark, LlmException.Http(400, "bad request")), [kimi]);
        var error = await Assert.ThrowsAsync<LlmException>(() => Text(client));
        Assert.Equal(400, error.StatusCode);
        Assert.Empty(kimi.Requests);
        // Nor is a conversation too long for the window.
        var overflow = new FailoverClient(Down(Spark, LlmException.Overflow(1000, "too long")), [kimi]);
        Assert.Equal(LlmErrorKind.Overflow, (await Assert.ThrowsAsync<LlmException>(() => Text(overflow))).Kind);
        Assert.Empty(kimi.Requests);
    }

    [Fact]
    public async Task AReplyThatHasStartedIsNeverMoved()
    {
        var kimi = Up(Kimi, "ok");
        var primary = new FakeProviderClient(Spark, (_, _) =>
            FakeProviderClient.Fails(LlmException.Sse("the reply was cut off"), new LlmStreamEvent.Text("half an ans")));
        var client = new FailoverClient(primary, [kimi]);
        var seen = new List<string>();
        var error = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var ev in client.StreamAsync(Request))
                if (ev is LlmStreamEvent.Text t) seen.Add(t.Delta);
        });
        Assert.Equal(LlmErrorKind.Sse, error.Kind);
        Assert.Equal(["half an ans"], seen);
        Assert.Empty(kimi.Requests);
    }

    [Fact]
    public async Task WhenEveryoneFailsThePrimarysErrorSurfaces()
    {
        var notices = new List<FailoverNotice>();
        var client = new FailoverClient(Down(Spark, LlmException.Connection("connection refused")),
            [Down(Kimi, LlmException.Http(429, "limit")), Down(Halo, LlmException.Http(500, "boom"))], notices.Add);
        var error = await Assert.ThrowsAsync<LlmException>(() => Text(client));
        Assert.Equal(LlmErrorKind.Connection, error.Kind); // so the usual retry and back-off carry on
        Assert.Empty(notices);
    }

    [Fact]
    public async Task ABackupThatServesNothingIsSkippedQuickly()
    {
        var silent = new FakeProviderClient(Halo, (_, _) => FakeProviderClient.Text("never")) { Served = [] };
        var kimi = Up(Kimi, "ok");
        var client = new FailoverClient(Down(Spark), [silent, kimi]);
        Assert.Equal("ok", await Text(client));
        Assert.Empty(silent.Requests); // asked what it serves, not sent the chat
    }

    [Fact]
    public async Task ABoxOfYourOwnAnswersWithTheModelItServesNow()
    {
        var halo = new FakeProviderClient(Halo, (_, _) => FakeProviderClient.Text("ok")) { Served = ["haloflash"] };
        await Text(new FailoverClient(Down(Spark), [halo]));
        Assert.Equal("haloflash", halo.Requests.Single().Model);
    }

    [Fact]
    public void BackupsAreTheEnabledRoutesMarkedSoInListOrder()
    {
        ProviderProfile[] routes =
        [
            Spark with { Backup = true },                              // the primary is never its own backup
            Kimi with { Backup = true, Enabled = false },              // off
            Halo,
            new(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt-5") { Backup = true },
            new(ProviderKind.OpenAICompat, "Other", "http://x.lan/v1", "m"),
        ];
        Assert.Equal(["Strix Halo", "OpenAI"], RouteRoster.Backups(routes, Spark.RouteId).Select(r => r.Name));
    }

    [Fact]
    public void TheMenuGroupsRoutesByRoleAndLeavesOutDisabledOnes()
    {
        ProviderProfile[] routes =
        [
            Spark,
            Kimi with { SubagentWorker = true, Backup = true },
            Halo,
            new(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt-5"),
            new(ProviderKind.OpenAICompat, "Off", "http://x.lan/v1", "m") { Enabled = false },
        ];
        var groups = RouteRoster.Groups(routes, Spark.RouteId);
        Assert.Equal([RouteRole.Primary, RouteRole.Subagent, RouteRole.Backup, RouteRole.Other], groups.Select(g => g.Role));
        Assert.Equal(["Spark"], groups[0].Routes.Select(r => r.Name));
        Assert.Equal(["Kimi Code"], groups[1].Routes.Select(r => r.Name));
        Assert.Equal(["Strix Halo"], groups[2].Routes.Select(r => r.Name));
        Assert.Equal(["OpenAI"], groups[3].Routes.Select(r => r.Name));
        Assert.Equal(["Primary", "Subagents", "Backups", "Other"], groups.Select(g => g.Role.Title()));
    }

    [Fact]
    public void WhichFailuresMoveATurn()
    {
        Assert.True(FailoverClient.ShouldFailOver(LlmException.Connection("refused")));
        Assert.True(FailoverClient.ShouldFailOver(LlmException.Sse("no data from the server for 600 s")));
        Assert.True(FailoverClient.ShouldFailOver(new HttpRequestException("reset")));
        Assert.True(FailoverClient.ShouldFailOver(new TimeoutException()));
        Assert.False(FailoverClient.ShouldFailOver(new OperationCanceledException()));
        Assert.False(FailoverClient.ShouldFailOver(LlmException.Http(400, "x")));
        Assert.False(FailoverClient.ShouldFailOver(LlmException.Http(413, "x")));
        Assert.False(FailoverClient.ShouldFailOver(LlmException.NoModel()));
        Assert.False(FailoverClient.ShouldFailOver(new InvalidOperationException()));
    }
}
