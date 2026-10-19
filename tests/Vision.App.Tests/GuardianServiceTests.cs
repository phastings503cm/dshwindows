using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.App.Views.Guide;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GuardianSerial
{
    public const string Name = "Guardian (process-wide secret store)";
}

/// <summary>The app's Guardian connection against a scripted Guardian: sign-in (password, 2FA, browser), the token as every
/// Guardian route's key, org switching, sign-out, prompt sync with the outcome (through the host), and refusals shown once.</summary>
[Collection(GuardianSerial.Name)]
public sealed class GuardianServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-guardian-{Guid.NewGuid():N}");
    private readonly bool _memoryOnly = SecretStore.UseMemoryOnly;

    public GuardianServiceTests()
    {
        Directory.CreateDirectory(_dir);
        SecretStore.UseMemoryOnly = true;
    }

    public void Dispose()
    {
        try { SecretStore.Delete(GuardianAccountStore.TokenTarget); } catch (Exception) { }
        SecretStore.UseMemoryOnly = _memoryOnly;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Guardian's harness API, scripted.</summary>
    private sealed class FakeGuardian : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body, Dictionary<string, string> Headers)> Requests = [];
        public Func<string, string, string, HttpResponseMessage>? Override;
        public bool TwoFactor;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add((request.Method.Method, path, body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            if (Override?.Invoke(request.Method.Method, path, body) is { } custom) return custom;
            return (request.Method.Method, path) switch
            {
                ("POST", "/api/auth/login") when TwoFactor => Json("""{"ok":true,"twoFactorRequired":true,"challenge":"chal"}"""),
                ("POST", "/api/auth/login") or ("POST", "/api/auth/login/2fa") =>
                    Json("""{"ok":true,"token":"tok-pw","session":{"id":"s1","userId":"u1","email":"pat@503cm.com","name":"Pat"},"user":{"id":"u1","email":"pat@503cm.com","name":"Pat"}}"""),
                ("POST", "/api/harness/connect/token") =>
                    Json("""{"ok":true,"token":"tok-browser","session":{"id":"s2","userId":"u1","email":"pat@503cm.com","name":"Pat"},"orgId":"o2"}"""),
                ("GET", "/api/harness/me") => Json("""
                    {"user":{"id":"u1","email":"pat@503cm.com","name":"Pat"},"orgs":[{"id":"o1","name":"503CM","active":true},{"id":"o2","name":"Cyticorp","active":false}],
                     "orgId":"o1","modules":["chat"],"permissions":["chat.create"],"limits":{"limits":[{"kind":"spend","window":"week","used":1,"max":50,"unit":"usd","resetsAt":null}]}}
                    """),
                ("GET", "/api/harness/v1/models") => Json("""{"models":[{"id":"guardian/default","label":"Default","contextWindow":200000}],"data":[]}"""),
                ("POST", "/api/harness/prompts") => Json("""{"accepted":1}"""),
                _ => Json("""{"error":"not_found","status":404,"message":"nope"}""", HttpStatusCode.NotFound),
            };
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public List<JsonNode> Prompts()
        {
            lock (Requests) return Requests.Where(r => r.Path == "/api/harness/prompts").Select(r => JsonNode.Parse(r.Body)!["prompts"]![0]!).ToList();
        }
    }

    private (AppConfig Config, AgentHost Host, GuardianService Guardian, FakeGuardian Fake) Make()
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        var host = new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")));
        var fake = new FakeGuardian();
        var guardian = new GuardianService(config, host, () => null, _dir, () => fake);
        return (config, host, guardian, fake);
    }

    private static async Task WaitUntil(string what, Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void ASignInGivesTheTokenToEveryGuardianRouteAndLearnsTheAccount() => UiThread.Run(async () =>
    {
        var (config, _, guardian, fake) = Make();
        var existing = GuardianRoutes.Route("https://guardian.example.test", new GuardianModel("guardian/fast"), null);
        config.Store(existing);

        Assert.True(await guardian.SignInAsync("https://guardian.example.test/", "pat@503cm.com", "pw"));

        Assert.Equal("tok-pw", config.ApiKey(existing));
        Assert.Equal("tok-pw", SecretStore.Read(GuardianAccountStore.TokenTarget));
        Assert.Equal("503CM", guardian.Account!.OrgName);
        Assert.Equal(2, guardian.Identity!.Orgs.Count);
        Assert.Equal("guardian/default", Assert.Single(guardian.Models).Id);
        Assert.Equal("$1.00 of $50.00 this week", Assert.Single(guardian.Limits).Describe());
        Assert.Equal("vision-windows", JsonNode.Parse(fake.Requests[0].Body)!["client"]!.GetValue<string>());
        Assert.DoesNotContain("tok-pw", File.ReadAllText(Path.Combine(_dir, "guardian.json")));

        // Another start finds the sign-in.
        var again = new GuardianService(config, null!, () => null, _dir, () => fake);
        Assert.True(again.IsSignedIn);
        Assert.Equal("pat@503cm.com", again.Account!.Email);
    });

    [Fact]
    public void A2faAccountStopsForTheCodeThenSignsIn() => UiThread.Run(async () =>
    {
        var (_, _, guardian, fake) = Make();
        fake.TwoFactor = true;
        Assert.False(await guardian.SignInAsync("https://guardian.example.test", "pat@503cm.com", "pw"));
        Assert.Equal("chal", guardian.PendingChallenge);
        Assert.False(guardian.IsSignedIn);

        Assert.False(await guardian.VerifyTwoFactorAsync("12"));
        Assert.Contains("6-digit", guardian.Status);
        Assert.True(await guardian.VerifyTwoFactorAsync("123456"));
        Assert.True(guardian.IsSignedIn);
        Assert.Null(guardian.PendingChallenge);
        Assert.Contains(fake.Requests, r => r.Path == "/api/auth/login/2fa" && r.Body.Contains("\"chal\""));
    });

    [Fact]
    public void TheBrowserSignInIsBoundToTheOrgPickedAtConsent() => UiThread.Run(async () =>
    {
        var (_, _, guardian, _) = Make();
        Task? browser = null;
        guardian.OpenBrowser = url =>
        {
            var query = GuardianLoopbackReceiver.ParseQuery(new Uri(url).PathAndQuery);
            browser = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetStringAsync($"{query["redirect_uri"]}?code=c1&state={Uri.EscapeDataString(query["state"])}");
            });
        };
        Assert.True(await guardian.SignInWithBrowserAsync("https://guardian.example.test"));
        await browser!;
        Assert.Equal("browser", guardian.Account!.Method);
        Assert.Equal("o2", guardian.Account.OrgId);
        Assert.Null(guardian.BrowserUrl);
        Assert.Equal("tok-browser", SecretStore.Read(GuardianAccountStore.TokenTarget));
    });

    [Fact]
    public void AModelThroughGuardianIsARouteWithTheTokenAndSwitchingOrgMovesItsHeader() => UiThread.Run(async () =>
    {
        var (config, _, guardian, fake) = Make();
        await guardian.SignInAsync("https://guardian.example.test", "pat@503cm.com", "pw");

        var route = guardian.UseModel(new GuardianModel("guardian/default", ContextWindow: 200_000), RouteUse.Primary)!;
        Assert.Equal("Guardian|guardian/default", config.ActiveRoute);
        Assert.Equal("https://guardian.example.test/api/harness/v1", config.ActiveProvider!.BaseUrl);
        Assert.Equal("tok-pw", config.ActiveProvider.ApiKey);
        Assert.Equal("o1", GuardianRoutes.OrgOf(config.ActiveProvider));

        await guardian.SwitchOrgAsync(guardian.Identity!.Orgs.Single(o => o.Id == "o2"));
        Assert.Equal("o2", GuardianRoutes.OrgOf(config.Providers.Single(p => p.RouteId == route.RouteId)));
        Assert.Equal("o2", fake.Requests[^1].Headers["x-guardian-org"]);
        Assert.Equal("Cyticorp", guardian.Account!.OrgName);
    });

    [Fact]
    public void SigningOutTakesTheTokenFromTheRoutesAndLetsGoOfGuardiansSecrets() => UiThread.Run(async () =>
    {
        var (config, host, guardian, _) = Make();
        await guardian.SignInAsync("https://guardian.example.test", "pat@503cm.com", "pw");
        var route = guardian.UseModel(new GuardianModel("guardian/default"), RouteUse.JustAdd)!;
        host.Vault.HoldForSession("FRESHDESK_API_KEY", "fd-value", tags: [GuardianService.VaultTag]);

        guardian.SignOut();

        Assert.False(guardian.IsSignedIn);
        Assert.Equal("", config.ApiKey(route));
        Assert.Null(SecretStore.Read(GuardianAccountStore.TokenTarget));
        Assert.Empty(host.Vault.SessionEntries);
        Assert.Equal(("https://guardian.example.test", "pat@503cm.com"), guardian.Remembered());
    });

    /// <summary>Guardian's routes to the scripted Guardian, everything else to the fake model server.</summary>
    private sealed class Split(HttpMessageHandler guardian, HttpMessageHandler model) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _guardian = new(guardian, disposeHandler: false);
        private readonly HttpMessageInvoker _model = new(model, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) ? _guardian : _model).SendAsync(request, cancellationToken);
    }

    /// <summary>A chat on a model server of its own (not Guardian's), with the fake model answering. It works in a project
    /// folder under the test's own: a chat without a project would make its scratch folder in the real data folder.</summary>
    private SessionVM ChatOnOwnServer(AppConfig config, AgentHost host, FakeGuardian fake, FakeModelServer model)
    {
        config.Activate(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "stub-model"));
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        model.Reset((_, _) => new FakeReply.Text("Done."));
        host.HttpHandlerForTesting = new Split(fake, model);
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(project);
        return host.NewSession(project);
    }

    [Fact]
    public void APromptGoesToTheAccountsNotebookWhenItStartsAndAgainWithHowItEnded() => UiThread.Run(async () =>
    {
        var (config, host, guardian, fake) = Make();
        var model = new FakeModelServer();
        try
        {
            await guardian.SignInAsync("https://guardian.example.test", "pat@503cm.com", "pw");
            var vm = ChatOnOwnServer(config, host, fake, model);
            vm.Title = "Inventory";

            // Signed in, a chat on any model: the prompt reaches the notebook once from the chat's timeline (one row).
            host.Send("Fix the failing test", vm.Id);
            await WaitUntil("the prompt and its ending went up", () => fake.Prompts().Count(p => p["outcome"] is not null) == 1);
            var prompts = fake.Prompts();
            Assert.Equal(2, prompts.Count);
            var (first, second) = (prompts[0], prompts[1]);
            Assert.Equal("Fix the failing test", first["text"]!.GetValue<string>());
            Assert.Equal("vision-windows", first["source"]!.GetValue<string>());
            Assert.Equal(vm.Id, first["chatId"]!.GetValue<string>());
            Assert.Null(first["outcome"]);
            // Same text and time: Guardian updates the row it has.
            Assert.Equal(first["at"]!.GetValue<string>(), second["at"]!.GetValue<string>());
            Assert.Equal("completed", second["outcome"]!.GetValue<string>());
            Assert.Equal("stub-model", second["model"]!.GetValue<string>());
            Assert.NotNull(second["durationSeconds"]);
            // With the account's token and org.
            (string Method, string Path, string Body, Dictionary<string, string> Headers) sent;
            lock (fake.Requests) sent = fake.Requests.First(r => r.Path == "/api/harness/prompts");
            Assert.Equal("Bearer tok-pw", sent.Headers["Authorization"]);
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });

    [Fact]
    public void NotSignedInNothingIsSent() => UiThread.Run(async () =>
    {
        var (config, host, _, fake) = Make();
        var model = new FakeModelServer();
        try
        {
            var vm = ChatOnOwnServer(config, host, fake, model);
            host.Send("hello", vm.Id);
            await WaitUntil("the turn ended", () => vm.PromptHistory.Count == 1 && vm.PromptHistory[0].Outcome != PromptOutcome.Running);
            await Task.Delay(100);
            lock (fake.Requests) Assert.Empty(fake.Requests);
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });

    [Fact]
    public void TheSameRefusalIsShownOnceNotOncePerRetry() => UiThread.Run(async () =>
    {
        var (_, _, guardian, _) = Make();
        var shown = new List<GuardianError>();
        guardian.Refused += shown.Add;
        var budget = GuardianError.FromResponse(402, """{"error":"budget_exceeded","status":402,"message":"Over budget"}""");
        guardian.Report(budget);
        guardian.Report(budget);
        guardian.Report(GuardianError.FromResponse(403, """{"error":"forbidden","status":403,"reason":"module_disabled","needed":"files","message":"No files"}"""));
        guardian.Report(GuardianError.FromResponse(500, "{}")); // not a refusal: the page's status line says it
        Assert.Equal(["Over budget", "No files"], shown.Select(e => e.Message));
        await Task.CompletedTask;
    });
}
