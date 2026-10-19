using System.Net;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>Browser sign-in: PKCE per RFC 7636 and the loopback redirect, exercised over a real 127.0.0.1 socket (the
/// "browser" is an HttpClient following Guardian's redirect).</summary>
public sealed class GuardianBrowserSignInTests
{
    [Fact]
    public void PkceMatchesRfc7636sExample()
    {
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GuardianPkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var pkce = GuardianPkce.Create();
        Assert.Equal(43, pkce.Verifier.Length);
        Assert.DoesNotContain('=', pkce.Verifier + pkce.Challenge + pkce.State);
        Assert.DoesNotContain('+', pkce.Verifier + pkce.Challenge + pkce.State);
        Assert.NotEqual(pkce.State, GuardianPkce.Create().State);
        Assert.DoesNotContain(pkce.Verifier, pkce.ToString());
    }

    [Fact]
    public void TheConsentAddressCarriesEverythingGuardianChecks()
    {
        var pkce = new GuardianPkce("v", "chal-lenge", "st/ate");
        var url = new Uri(GuardianBrowserSignIn.ConnectUrl("https://guardian.503cm.com", "http://127.0.0.1:53999/callback", pkce, "Vision for Windows on PAT-PC"));
        Assert.Equal("/app/connect/vision", url.AbsolutePath);
        var query = GuardianLoopbackReceiver.ParseQuery(url.PathAndQuery);
        Assert.Equal("http://127.0.0.1:53999/callback", query["redirect_uri"]);
        Assert.Equal("st/ate", query["state"]);
        Assert.Equal("chal-lenge", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("Vision for Windows on PAT-PC", query["device"]);
        // Guardian's consent page reads it: "Sign in to Vision for Windows", and the sign-in is tagged as this app's.
        Assert.Equal("vision-windows", query["client"]);
    }

    private static async Task<(HttpStatusCode Status, string Body)> Browse(string url)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(url);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheRedirectWithTheRightStateGivesTheCode()
    {
        using var receiver = GuardianLoopbackReceiver.Start();
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/callback$", receiver.RedirectUri);
        var wait = receiver.WaitForCodeAsync("s1", TimeSpan.FromSeconds(20));

        // A stray request and an old tab's answer are turned away; the wait goes on.
        var stray = await Browse($"http://127.0.0.1:{receiver.Port}/favicon.ico");
        Assert.Equal(HttpStatusCode.NotFound, stray.Status);
        var stale = await Browse(receiver.RedirectUri + "?code=old&state=other");
        Assert.Equal(HttpStatusCode.BadRequest, stale.Status);
        Assert.False(wait.IsCompleted);

        var page = await Browse(receiver.RedirectUri + "?code=abc%2B123&state=s1");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("You can close this tab", page.Body);
        Assert.Equal("abc+123", await wait);
    }

    [Fact]
    public async Task DenyEndsTheWaitInPlainWords()
    {
        using var receiver = GuardianLoopbackReceiver.Start();
        var wait = receiver.WaitForCodeAsync("s1", TimeSpan.FromSeconds(20));
        var page = await Browse(receiver.RedirectUri + "?error=access_denied&state=s1");
        Assert.Contains("You chose not to connect Vision", page.Body);
        var error = await Assert.ThrowsAsync<GuardianError>(() => wait);
        Assert.Equal(GuardianErrorKind.BrowserSignIn, error.Kind);
        Assert.Equal("You chose Deny in Guardian, so Vision isn't connected.", error.Message);
    }

    [Fact]
    public async Task NoAnswerTimesOut()
    {
        using var receiver = GuardianLoopbackReceiver.Start();
        var error = await Assert.ThrowsAsync<GuardianError>(() => receiver.WaitForCodeAsync("s1", TimeSpan.FromMilliseconds(300)));
        Assert.Equal(GuardianErrorKind.BrowserSignIn, error.Kind);
        Assert.StartsWith("Nothing came back from the browser", error.Message);
    }

    [Fact]
    public async Task TheWholeFlowOpensTheBrowserFollowsTheRedirectAndTradesTheCode()
    {
        var guardian = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"ok":true,"token":"harness-token","session":{"id":"s","userId":"u1","email":"pat@503cm.com","name":"Pat"},"orgId":"org-1"}
            """));
        var client = new GuardianClient("https://guardian.example.test", handler: guardian);
        string? opened = null;
        Task? browser = null;

        var session = await GuardianBrowserSignIn.SignInAsync(client, url =>
        {
            opened = url;
            // Guardian's consent page: the person approves, and it redirects to the loopback address with the code.
            var query = GuardianLoopbackReceiver.ParseQuery(new Uri(url).PathAndQuery);
            browser = Browse($"{query["redirect_uri"]}?code=one-time&state={Uri.EscapeDataString(query["state"])}");
        }, device: "TEST-PC", timeout: TimeSpan.FromSeconds(20));
        await browser!;

        Assert.StartsWith("https://guardian.example.test/app/connect/vision?", opened);
        Assert.Equal("harness-token", session.Token);
        Assert.Equal("org-1", session.OrgId);
        var exchange = JsonNode.Parse(Assert.Single(guardian.Requests).Body)!;
        Assert.Equal("one-time", exchange["code"]!.GetValue<string>());
        var query = GuardianLoopbackReceiver.ParseQuery(new Uri(opened!).PathAndQuery);
        // The verifier sent with the code is the one whose S256 the consent page was given.
        Assert.Equal(query["code_challenge"], GuardianPkce.ChallengeFor(exchange["code_verifier"]!.GetValue<string>()));
        Assert.Equal(query["redirect_uri"], exchange["redirect_uri"]!.GetValue<string>());
    }
}
