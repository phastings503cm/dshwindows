using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>The Guardian client against a scripted transport: the shapes are the ones Guardian's harness API answers with
/// (GuardianProject feat/harness-api: auth.controller login, harness-errors.ts, harness.controller.ts).</summary>
public sealed class GuardianClientTests
{
    private const string Address = "https://guardian.example.test";

    private static GuardianSession Signed(string token = "tok-123") => new("u1", "ada@503cm.com", "Ada", token, DateTimeOffset.Now);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, Action<HttpResponseMessage>? headers = null)
    {
        var response = FakeHttpHandler.Json(json, status);
        headers?.Invoke(response);
        return response;
    }

    // MARK: Addresses

    [Theory]
    [InlineData("https://guardian.503cm.com", "https://guardian.503cm.com")]
    [InlineData("guardian.503cm.com/", "https://guardian.503cm.com")]
    [InlineData("  https://guardian.503cm.com/api/harness/v1  ", "https://guardian.503cm.com")]
    [InlineData("https://dev.guardian.503cm.com/", "https://dev.guardian.503cm.com")]
    [InlineData("http://127.0.0.1:3001", "http://127.0.0.1:3001")]
    [InlineData("http://192.168.1.20:3001/", "http://192.168.1.20:3001")]
    public void AddressesAreTidied(string typed, string expected) => Assert.Equal(expected, GuardianClient.NormalizeAddress(typed));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://guardian.503cm.com")]
    [InlineData("http://guardian.503cm.com")] // plain http only to this PC or the local network
    [InlineData("not a url at all")]
    public void NonAddressesAreRefused(string typed)
    {
        Assert.Null(GuardianClient.NormalizeAddress(typed));
        if (typed.Length > 0) Assert.Equal(GuardianErrorKind.BadAddress, Assert.Throws<GuardianError>(() => new GuardianClient(typed)).Kind);
    }

    // MARK: Sign-in

    [Fact]
    public async Task PasswordSignInSendsTheHarnessClientAndReadsGuardiansAnswer()
    {
        // Guardian 0.111's answer: session carries id and userId, and user is there too.
        var handler = new FakeHttpHandler(_ => Json("""
            {"ok":true,"token":"eyJhbGciOi.payload.sig","session":{"id":"s9","userId":"u1","email":"ada@503cm.com","name":"Ada Lovelace"},
             "user":{"id":"u1","email":"ada@503cm.com","name":"Ada Lovelace"}}
            """));
        var client = new GuardianClient(Address, handler: handler);

        var session = await client.SignInAsync(" ada@503cm.com ", "pa55word");

        Assert.Equal("eyJhbGciOi.payload.sig", session.Token);
        Assert.Equal("u1", session.UserId);
        Assert.Equal("Ada Lovelace", session.Name);
        Assert.True(client.IsSignedIn);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal($"{Address}/api/auth/login", sent.Url.ToString());
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("ada@503cm.com", body["email"]!.GetValue<string>());
        Assert.Equal("pa55word", body["password"]!.GetValue<string>());
        Assert.Equal("vision-windows", body["client"]!.GetValue<string>());
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        // The password never shows in a session's printout.
        Assert.DoesNotContain("eyJ", session.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOlderLoginShapeStillSignsIn()
    {
        // Guardian before the harness slice: {ok, session:{userId, email}, token}.
        var client = new GuardianClient(Address, handler: new FakeHttpHandler(_ => Json("""{"ok":true,"session":{"userId":"u7","email":"sam@503cm.com"},"token":"t7"}""")));
        var session = await client.SignInAsync("sam@503cm.com", "pw");
        Assert.Equal(("u7", "sam@503cm.com", "t7"), (session.UserId, session.Email, session.Token));
    }

    [Fact]
    public async Task AWrongPasswordIsACredentialsProblemNotAnExpiredSignIn()
    {
        var client = new GuardianClient(Address, handler: new FakeHttpHandler(_ =>
            Json("""{"statusCode":401,"message":"Invalid email or password","error":"Unauthorized"}""", HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<GuardianError>(() => client.SignInAsync("ada@503cm.com", "nope"));
        Assert.Equal(GuardianErrorKind.Credentials, error.Kind);
        Assert.Equal("Guardian didn't accept that sign-in: Invalid email or password", error.Message);
        Assert.False(client.IsSignedIn);
    }

    [Fact]
    public async Task ATotpAccountAsksForTheCodeThenFinishesWithIt()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == "/api/auth/login"
            ? Json("""{"ok":true,"twoFactorRequired":true,"challenge":"chal.jwt"}""")
            : Json("""{"ok":true,"token":"tok-2fa","session":{"id":"s1","userId":"u1","email":"ada@503cm.com","name":"Ada"}}"""));
        var client = new GuardianClient(Address, handler: handler);

        var asked = await Assert.ThrowsAsync<GuardianError>(() => client.SignInAsync("ada@503cm.com", "pw"));
        Assert.Equal(GuardianErrorKind.TwoFactorRequired, asked.Kind);
        Assert.Equal("chal.jwt", asked.Challenge);
        Assert.False(client.IsSignedIn);

        var session = await client.VerifyTwoFactorAsync("chal.jwt", " 123 456 ", "ada@503cm.com");
        Assert.Equal("tok-2fa", session.Token);
        var verify = handler.Requests[^1];
        Assert.Equal($"{Address}/api/auth/login/2fa", verify.Url.ToString());
        Assert.Equal("123456", JsonNode.Parse(verify.Body)!["code"]!.GetValue<string>());
        Assert.Equal("chal.jwt", JsonNode.Parse(verify.Body)!["challenge"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWrongCodeSaysCodesRotate()
    {
        var client = new GuardianClient(Address, handler: new FakeHttpHandler(_ =>
            Json("""{"statusCode":401,"message":"Invalid code","error":"Unauthorized"}""", HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<GuardianError>(() => client.VerifyTwoFactorAsync("chal", "000000"));
        Assert.Equal(GuardianErrorKind.Credentials, error.Kind);
        Assert.Contains("Codes refresh every 30 seconds", error.Message);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public async Task AMalformedCodeIsRefusedWithoutAsking(string code)
    {
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("must not be called"));
        var error = await Assert.ThrowsAsync<GuardianError>(() => new GuardianClient(Address, handler: handler).VerifyTwoFactorAsync("chal", code));
        Assert.Equal(GuardianErrorKind.Credentials, error.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheBrowserCodeIsTradedWithItsVerifierForAnOrgBoundToken()
    {
        var handler = new FakeHttpHandler(_ => Json("""
            {"ok":true,"token":"harness-tok","session":{"id":"s2","userId":"u1","email":"ada@503cm.com","name":"Ada"},"user":{"id":"u1","email":"ada@503cm.com","name":"Ada"},"orgId":"org-503"}
            """));
        var client = new GuardianClient(Address, handler: handler);
        var session = await client.ExchangeConnectCodeAsync("code-1", "verifier-1", "http://127.0.0.1:53999/callback");
        Assert.Equal("org-503", session.OrgId);
        Assert.Equal("org-503", client.OrgId);
        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
        Assert.Equal("code-1", body["code"]!.GetValue<string>());
        Assert.Equal("verifier-1", body["code_verifier"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:53999/callback", body["redirect_uri"]!.GetValue<string>());
    }

    // MARK: Identity, limits, models

    [Fact]
    public async Task MeReadsOrgsModulesAndTheNestedLimits()
    {
        var handler = new FakeHttpHandler(_ => Json("""
            {"user":{"id":"u1","email":"ada@503cm.com","name":"Ada"},"orgs":[{"id":"o1","name":"503CM","active":true},{"id":"o2","name":"Cyticorp","active":false}],
             "orgId":"o1","modules":["chat","files"],"permissions":["chat.read","chat.create"],
             "limits":{"limits":[{"kind":"spend","window":"week","used":3.2,"max":50,"unit":"usd","resetsAt":"2026-10-26T00:00:00.000Z"}]},
             "session":{"id":"s1"}}
            """));
        var me = await new GuardianClient(Address, Signed(), handler).MeAsync();
        Assert.Equal(2, me.Orgs.Count);
        Assert.Equal("503CM", me.ActiveOrg!.Name);
        Assert.Equal(["chat", "files"], me.Modules);
        var limit = Assert.Single(me.Limits);
        Assert.Equal("$3.20 of $50.00 this week", limit.Describe());
        Assert.Equal(46.8, limit.Remaining!.Value, 3);
        Assert.Equal("Bearer tok-123", handler.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task TheOrgPickedGoesOnEveryCall()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"limits":[{"kind":"requests","window":"run","used":2,"max":null,"unit":"streams","resetsAt":null}]}"""));
        var client = new GuardianClient(Address, Signed(), handler) { OrgId = "o2" };
        var limit = Assert.Single(await client.LimitsAsync());
        Assert.Equal("o2", handler.Requests[0].Headers["x-guardian-org"]);
        Assert.Equal("2 streams used this run", limit.Describe());
        Assert.Null(limit.Fraction);
    }

    [Fact]
    public async Task ModelsComeFromTheRichListOrTheOpenAiOne()
    {
        var rich = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => Json("""
            {"object":"list","models":[{"id":"guardian/default","label":"Guardian default","contextWindow":200000,"interactiveOnly":false},
             {"id":"us.anthropic.claude-sonnet-5-5","label":"Claude Sonnet 5.5","contextWindow":1000000,"maxOutputTokens":64000}],
             "data":[{"id":"guardian/default","object":"model"}]}
            """)));
        var models = await rich.ModelsAsync();
        Assert.Equal(["guardian/default", "us.anthropic.claude-sonnet-5-5"], models.Select(m => m.Id));
        Assert.True(models[0].IsAlias);
        Assert.Equal(1_000_000, models[1].ContextWindow);
        Assert.Equal("Claude Sonnet 5.5 (us.anthropic.claude-sonnet-5-5)", models[1].Title);

        var plain = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => Json("""{"data":[{"id":"m1","object":"model"}]}""")));
        Assert.Equal("m1", Assert.Single(await plain.ModelsAsync()).Id);
        Assert.Equal($"{Address}/api/harness/v1", plain.ProxyBaseUrl);
    }

    // MARK: Sync

    [Fact]
    public async Task PromptsGoUpWithTheirChatAndComeBackNewestFirst()
    {
        var handler = new FakeHttpHandler(request => request.Method == HttpMethod.Post
            ? Json("""{"accepted":1}""")
            : Json("""{"prompts":[{"id":"p1","text":"Fix the build","at":"2026-10-19T07:00:00.000Z","outcome":"completed","source":"vision-mac","chatTitle":"Build","project":"inventory","model":"m"}]}"""));
        var client = new GuardianClient(Address, Signed(), handler);
        var at = new DateTimeOffset(2026, 10, 19, 0, 1, 2, 345, TimeSpan.FromHours(-7));

        var accepted = await client.PushPromptsAsync([new GuardianPrompt("Fix the build", at)
        {
            ChatId = "chat-1", ChatTitle = "Build", Project = "inventory", Model = "qwen3", Outcome = "completed", DurationSeconds = 12.34,
        }]);

        Assert.Equal(1, accepted);
        var row = JsonNode.Parse(handler.Requests[0].Body)!["prompts"]![0]!;
        Assert.Equal("2026-10-19T07:01:02.345Z", row["at"]!.GetValue<string>());
        Assert.Equal("vision-windows", row["source"]!.GetValue<string>());
        Assert.Equal("chat-1", row["chatId"]!.GetValue<string>());
        Assert.Equal("completed", row["outcome"]!.GetValue<string>());
        Assert.Equal(12.3, row["durationSeconds"]!.GetValue<double>());

        var back = Assert.Single(await client.PromptsAsync(50));
        Assert.Equal(("Fix the build", "completed", "vision-mac"), (back.Text, back.Outcome, back.Source));
        Assert.Contains("limit=50", handler.Requests[1].Url.Query);
    }

    [Fact]
    public async Task SkillsChatsAndVaultRoundTrip()
    {
        var handler = new FakeHttpHandler(request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/harness/skills") => Json("""{"skills":[{"name":"deploy","description":"Ship it","files":{"SKILL.md":"---\nname: deploy\n---\nGo","scripts/run.ps1":"echo"}}]}"""),
            ("GET", "/api/harness/chats") => Json("""{"chats":[{"id":"a","title":"Old","updatedAt":"2026-10-01T00:00:00Z"},{"id":"b","title":"New","updatedAt":"2026-10-18T00:00:00Z"}]}"""),
            ("GET", "/api/harness/chats/b") => Json("""{"id":"b","title":"New","updatedAt":"2026-10-18T00:00:00Z","payload":"{\"id\":\"b\",\"rows\":[]}"}"""),
            ("GET", "/api/harness/vault") => Json("""{"secrets":[{"id":"v1","name":"FRESHDESK_API_KEY","kind":"api_key"}]}"""),
            ("POST", "/api/harness/vault/v1/reveal") => Json("""{"id":"v1","name":"FRESHDESK_API_KEY","value":"fd-secret-value"}"""),
            _ => Json("""{"ok":true}"""),
        });
        var client = new GuardianClient(Address, Signed(), handler);

        await client.PutSkillAsync(new GuardianSkill("deploy", "Ship it", new Dictionary<string, string> { ["SKILL.md"] = "x" }));
        var put = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.Equal("x", put["files"]!["SKILL.md"]!.GetValue<string>());

        var skill = Assert.Single(await client.SkillsAsync());
        Assert.Equal(2, skill.Files.Count);

        await client.PutChatAsync("b", "New", new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero), """{"id":"b","rows":[]}""");
        var chat = JsonNode.Parse(handler.Requests[2].Body)!;
        Assert.Equal("""{"id":"b","rows":[]}""", chat["payload"]!.GetValue<string>());
        Assert.Equal("2026-10-18T00:00:00.000Z", chat["updatedAt"]!.GetValue<string>());

        Assert.Equal(["New", "Old"], (await client.ChatsAsync()).Select(c => c.Title));
        Assert.Equal("""{"id":"b","rows":[]}""", await client.ChatAsync("b"));

        Assert.Equal("FRESHDESK_API_KEY", Assert.Single(await client.VaultAsync()).Name);
        Assert.Equal("fd-secret-value", await client.RevealAsync("v1"));
    }

    [Fact]
    public async Task ABigProjectGoesUpInOrderedChunksWithOneUploadId()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"ok":true}"""));
        var client = new GuardianClient(Address, Signed(), handler);
        var files = Enumerable.Range(0, 30).ToDictionary(i => $"src/file{i:00}.cs", _ => new string('x', 390_000));

        var chunks = await client.PutProjectAsync("inventory", files);

        Assert.True(chunks >= 2);
        Assert.Equal(chunks, handler.Requests.Count);
        var bodies = handler.Requests.Select(r => JsonNode.Parse(r.Body)!).ToList();
        Assert.Equal(Enumerable.Range(0, chunks), bodies.Select(b => b["chunk"]!.GetValue<int>()));
        Assert.All(bodies, b => Assert.Equal(chunks, b["totalChunks"]!.GetValue<int>()));
        Assert.Single(bodies.Select(b => b["uploadId"]!.GetValue<string>()).Distinct());
        Assert.All(handler.Requests, r => Assert.True(Encoding.UTF8.GetByteCount(r.Body) < GuardianClient.ProjectChunkBytes + 1_000));
        Assert.Equal(30, bodies.Sum(b => b["files"]!.AsObject().Count));

        // A small one is one plain call, without the chunk fields.
        var small = new FakeHttpHandler(_ => Json("{}"));
        await new GuardianClient(Address, Signed(), small).PutProjectAsync("tiny", new Dictionary<string, string> { ["a.txt"] = "a" });
        Assert.Null(JsonNode.Parse(Assert.Single(small.Requests).Body)!["chunk"]);
    }

    [Fact]
    public async Task NothingIsSentBeforeSignIn()
    {
        var handler = new FakeHttpHandler(_ => Json("{}"));
        var error = await Assert.ThrowsAsync<GuardianError>(() => new GuardianClient(Address, handler: handler).MeAsync());
        Assert.Equal(GuardianErrorKind.Unauthorized, error.Kind);
        Assert.Empty(handler.Requests);
    }

    // MARK: Refusals (harness-errors.ts)

    [Fact]
    public async Task A401SaysSignInAgain()
    {
        var client = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ =>
            Json("""{"error":"unauthorized","status":401,"message":"Your Guardian sign-in has ended or was revoked. Sign in again from Vision."}""", HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<GuardianError>(() => client.MeAsync());
        Assert.Equal(GuardianErrorKind.Unauthorized, error.Kind);
        var text = GuardianRefusalText.For(error);
        Assert.Equal("Sign in to Guardian again", text.Title);
        Assert.True(text.OffersSignIn);
        Assert.Equal("Your Guardian sign-in has ended or was revoked. Sign in again from Vision.", text.Message);
    }

    [Fact]
    public async Task A403NamesWhatIsMissingAndWhoCanGrantIt()
    {
        var client = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => Json("""
            {"error":"forbidden","status":403,"reason":"module_disabled","needed":"files","message":"My Files is not turned on for you in this Guardian org. Ask Pat Hastings or Sam Lee (admins of 503CM) to turn it on for you."}
            """, HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<GuardianError>(() => client.ProjectsAsync());
        Assert.Equal((GuardianErrorKind.Forbidden, "module_disabled", "files"), (error.Kind, error.Reason, error.Needed));
        var text = GuardianRefusalText.For(error);
        Assert.Equal("Guardian doesn't allow this yet", text.Title);
        Assert.StartsWith("My Files is not turned on for you", text.Message);
        Assert.Equal("Missing: files — that module is switched off for you", text.Details[0]);
        Assert.False(text.OffersSignIn);
    }

    [Fact]
    public async Task A402SaysTheBudgetWithItsNumbersAndReset()
    {
        var client = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => Json("""
            {"error":"budget_exceeded","status":402,"limit":{"kind":"spend","window":"day","used":5.01,"max":5,"unit":"usd","resetsAt":"2026-10-20T07:00:00.000Z"},"message":"You've used your $5.00 daily Vision budget. It resets at midnight."}
            """, HttpStatusCode.PaymentRequired, r =>
        {
            r.Headers.TryAddWithoutValidation("x-guardian-limit-kind", "spend");
            r.Headers.TryAddWithoutValidation("x-guardian-limit-window", "day");
            r.Headers.TryAddWithoutValidation("x-guardian-limit-remaining", "0");
            r.Headers.TryAddWithoutValidation("Retry-After", "3600");
        })));
        var error = await Assert.ThrowsAsync<GuardianError>(() => client.ModelsAsync());
        Assert.Equal(GuardianErrorKind.Limit, error.Kind);
        Assert.Equal("budget_exceeded", error.Code);
        Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
        var text = GuardianRefusalText.For(error);
        Assert.Equal("You've reached your Guardian budget", text.Title);
        Assert.Equal("Spend: $5.01 of $5.00 today", text.Details[0]);
        Assert.StartsWith("Resets ", text.Details[1]);
        Assert.Equal("Try again in 3600 s.", text.Details[2]);
    }

    [Fact]
    public void A429WithOnlyHeadersStillSaysTheLimit()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.TryAddWithoutValidation("x-guardian-limit-kind", "requests");
        response.Headers.TryAddWithoutValidation("x-guardian-limit-window", "run");
        response.Headers.TryAddWithoutValidation("x-guardian-limit-remaining", "0");
        response.Headers.TryAddWithoutValidation("Retry-After", "5");
        var error = GuardianError.FromResponse(429, "<html>Too many</html>", response.Headers);
        Assert.Equal((GuardianErrorKind.Limit, "rate_limited"), (error.Kind, error.Code));
        Assert.Equal("Guardian asks Vision to slow down for a moment.", error.Message);
        var text = GuardianRefusalText.For(error);
        Assert.Equal("Guardian asks Vision to slow down", text.Title);
        Assert.Equal("Requests: 0 requests left this run", text.Details[0]);
        Assert.Equal("Try again in 5 s.", text.Details[1]);
    }

    [Fact]
    public void AStorageCapIsALimitTooWithItsSizesInWords()
    {
        var error = GuardianError.FromResponse(409, """
            {"error":"limit_reached","status":409,"limit":{"kind":"storage","window":"total","used":4294967296,"max":4294967296,"unit":"bytes","resetsAt":null},"message":"Your Vision uploads use all 4 GB you have in Guardian. Delete an old project to make room."}
            """);
        Assert.Equal((GuardianErrorKind.Limit, 409, "limit_reached"), (error.Kind, error.Status, error.Code));
        var text = GuardianRefusalText.For(error);
        Assert.Equal("You've reached a Guardian limit", text.Title);
        Assert.Equal("Storage: 4 GB of 4 GB in all", text.Details[0]);
        // A plain conflict (a name taken) is not a limit.
        Assert.Equal(GuardianErrorKind.Conflict, GuardianError.FromResponse(409, """{"error":"name_taken","status":409,"message":"Taken"}""").Kind);
        Assert.Equal("1 of 30 requests this minute", new GuardianLimit("requests", "minute", 1, 30, null, null).Describe());
    }

    [Fact]
    public async Task APageThatIsntGuardiansApiSaysCheckTheAddress()
    {
        var client = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!doctype html><title>Sign in</title>", Encoding.UTF8, "text/html"),
        }));
        var error = await Assert.ThrowsAsync<GuardianError>(() => client.MeAsync());
        Assert.Equal(GuardianErrorKind.Server, error.Kind);
        Assert.Contains("check the address", error.Message);
    }

    [Fact]
    public async Task UnreachableIsANetworkProblem()
    {
        var client = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => throw new HttpRequestException("No such host is known.")));
        var error = await Assert.ThrowsAsync<GuardianError>(() => client.MeAsync());
        Assert.Equal(GuardianErrorKind.Network, error.Kind);
        Assert.Equal("Couldn't reach Guardian: No such host is known.", error.Message);
    }
}
