using System.Net;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>The shared-ETA client against the Guardian ⇄ Vision contract v1 (POST/GET /api/harness/eta), with a scripted
/// transport: what goes on the wire, how the org's means blend in, and Guardian's structured refusals.</summary>
public sealed class GuardianEtaTests
{
    private static readonly GuardianEndpoint Endpoint = new("https://guardian.example.test", "tok-123", "org-9");

    [Fact]
    public async Task AFinishedTurnsDurationIsPushedAsTheContractSays()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"accepted":1}"""));
        await new GuardianEtaClient(Endpoint, handler).PushAsync([(TaskKind.CodeEdit, 1.25, 184.5)]);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://guardian.example.test/api/harness/eta", sent.Url.ToString());
        Assert.Equal("Bearer tok-123", sent.Headers["Authorization"]);
        Assert.Equal("org-9", sent.Headers["x-guardian-org"]);
        var sample = JsonNode.Parse(sent.Body)!["samples"]![0]!;
        Assert.Equal(("codeEdit", 1.25, 184.5, "vision-windows"),
            ((string)sample["kind"]!, (double)sample["scope"]!, (double)sample["seconds"]!, (string)sample["harness"]!));
    }

    [Fact]
    public async Task NothingToPushSendsNothing()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}"));
        await new GuardianEtaClient(Endpoint, handler).PushAsync([]);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheOrgsMeansAreReadAndUnknownKindsSkipped()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"byKind":{"writing":{"count":7,"meanSeconds":95.5,"meanScope":1.1},"research":{"count":2,"meanSeconds":40},
             "hologram":{"count":9,"meanSeconds":1,"meanScope":1},"buildRun":{"count":"x","meanSeconds":1}}}
            """));
        var summary = await new GuardianEtaClient(Endpoint, handler).SummaryAsync();
        Assert.Equal(new DurationSample(7, 95.5, 1.1), summary[TaskKind.Writing]);
        Assert.Equal(new DurationSample(2, 40, 1), summary[TaskKind.Research]);
        Assert.Equal(2, summary.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task AKindThisPcHasNotLearnedIsAdoptedAndOneWithMoreEvidenceMerges()
    {
        var runner = new TurnPhaseRunner();
        runner.MergeSample(TaskKind.Writing, new DurationSample(2, 60, 1));
        runner.MergeSample(TaskKind.Research, new DurationSample(9, 30, 1));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"byKind":{"codeEdit":{"count":5,"meanSeconds":200,"meanScope":1.5},"writing":{"count":6,"meanSeconds":100,"meanScope":1},
             "research":{"count":3,"meanSeconds":90,"meanScope":1}}}
            """));
        var changed = await new GuardianEtaClient(Endpoint, handler).SyncIntoAsync(runner);

        Assert.Equal(new DurationSample(5, 200, 1.5), runner.Sample(TaskKind.CodeEdit)); // adopted whole
        var writing = runner.Sample(TaskKind.Writing);                                    // merged by count
        Assert.Equal(8, writing.Count);
        Assert.Equal(90, writing.MeanSeconds, 6);
        Assert.Equal(new DurationSample(9, 30, 1), runner.Sample(TaskKind.Research));     // this PC knows more: kept
        Assert.Equal([TaskKind.CodeEdit, TaskKind.Writing], changed.OrderBy(k => k));

        // The same numbers again change nothing.
        Assert.Empty(await new GuardianEtaClient(Endpoint, handler).SyncIntoAsync(runner));
    }

    [Fact]
    public async Task AForbiddenAnswerCarriesWhatIsMissing()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"error":"forbidden","reason":"module_disabled","needed":"vision","message":"The Vision module is off for 503CM. An org admin can turn it on."}
            """, HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsAsync<GuardianException>(() => new GuardianEtaClient(Endpoint, handler).SummaryAsync());
        Assert.Equal((403, "forbidden", "module_disabled", "vision"), (error.Status, error.Error, error.Reason, error.Needed));
        Assert.Equal("The Vision module is off for 503CM. An org admin can turn it on.", error.Message);
    }

    [Fact]
    public async Task ALimitOrAProxyPageStillSaysWhatHappened()
    {
        var limit = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"error":"limit_reached","limit":{"kind":"tokens","window":"day","used":10,"max":10,"unit":"tokens","resetsAt":"2026-10-19T00:00:00Z"},"message":"Today's token allowance is used up."}
            """, (HttpStatusCode)429));
        var error = await Assert.ThrowsAsync<GuardianException>(() => new GuardianEtaClient(Endpoint, limit).PushAsync([(TaskKind.Writing, 1, 10)]));
        Assert.Equal(("limit_reached", "Today's token allowance is used up."), (error.Error, error.Message));

        var proxy = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("<html>nope</html>") });
        var signedOut = await Assert.ThrowsAsync<GuardianException>(() => new GuardianEtaClient(Endpoint, proxy).SummaryAsync());
        Assert.Equal(("unauthorized", "Guardian didn't accept the sign-in — sign in again."), (signedOut.Error, signedOut.Message));
    }

    [Fact]
    public async Task APromptGoesToTheAccountsPromptHistoryWithItsOutcome()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"accepted":1}"""));
        var record = new PromptRecord
        {
            ChatId = "c1", Text = "Fix the parser", Source = PromptSource.Goal, Outcome = PromptOutcome.Completed, TurnId = "t1", Model = "qwen3",
            At = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero), FinishedAt = new DateTimeOffset(2026, 10, 18, 9, 1, 30, TimeSpan.Zero),
        };
        await new GuardianEtaClient(Endpoint, handler).PushPromptsAsync([(record, "Parser work", "inventory")]);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://guardian.example.test/api/harness/prompts", sent.Url.ToString());
        var prompt = JsonNode.Parse(sent.Body)!["prompts"]![0]!;
        Assert.Equal(("Fix the parser", "2026-10-18T09:00:00.000Z", "completed", "goal", "c1", "Parser work", "inventory", "qwen3", 90.0, "t1"),
            ((string)prompt["text"]!, (string)prompt["at"]!, (string)prompt["outcome"]!, (string)prompt["source"]!, (string)prompt["chatId"]!,
             (string)prompt["chatTitle"]!, (string)prompt["project"]!, (string)prompt["model"]!, (double)prompt["durationSeconds"]!, (string)prompt["runId"]!));
    }

    [Theory]
    [InlineData("https://guardian.503cm.com/api/harness/v1", "https://guardian.503cm.com")]
    [InlineData("https://dev.guardian.503cm.com/api/harness/v1/", "https://dev.guardian.503cm.com")]
    public void AGuardianRouteRevealsWhereGuardianIs(string baseUrl, string guardian)
    {
        var route = new ProviderProfile(ProviderKind.OpenAICompat, "Guardian", baseUrl, "guardian/default")
        {
            CustomHeaders = new Dictionary<string, string> { ["X-Guardian-Org"] = "org-1" },
        };
        Assert.Equal(new GuardianEndpoint(guardian, "tok", "org-1"), GuardianEndpoint.FromRoute(route, " tok "));
        Assert.Null(GuardianEndpoint.FromRoute(route, ""));
        Assert.Null(GuardianEndpoint.FromRoute(new ProviderProfile(ProviderKind.OpenAICompat, "Spark", "http://192.168.1.9:8000/v1", "qwen"), "k"));
    }
}
