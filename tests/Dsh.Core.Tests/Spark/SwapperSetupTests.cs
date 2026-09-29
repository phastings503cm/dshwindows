using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>A stand-in for Spark Swapper's app.py: the same routes, the same same-origin rule for
/// POSTs (X-Swapper header, Origin matching Host), cookie sessions, and the first visitor becoming
/// admin.</summary>
public sealed class FakeSwapper : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private (string User, string Password)? _admin;
    private const string Token = "c2Vzc2lvbg";
    public TestHttpServer Server { get; }
    public List<string> Installs { get; } = [];

    public FakeSwapper(bool withAdmin = false)
    {
        if (withAdmin) _admin = ("admin", "correct horse");
        TestHttpServer? server = null;
        server = new TestHttpServer(r => Handle(r, server!), tls: true);
        Server = server;
    }

    public Uri Url => new($"https://127.0.0.1:{Server.Port}");
    public string Fingerprint => SparkSwapperClient.Fingerprint(Server.Certificate!.RawData);

    private static TestHttpServer.Response Json(int status, string body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, body, "application/json", headers);

    private static readonly Dictionary<string, string> SetCookie = new()
    {
        ["Set-Cookie"] = $"swapper_session={Token}; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=2592000",
    };

    private TestHttpServer.Response Handle(TestHttpServer.Request request, TestHttpServer server)
    {
        var path = request.Path.Split('?')[0];
        var signedIn = request.Headers.TryGetValue("Cookie", out var cookie) && cookie.Contains($"swapper_session={Token}");
        if (request.Method == "GET")
        {
            switch (path)
            {
                case "/api/session":
                    lock (_lock)
                        return Json(200, $$"""{"setup_needed": {{(_admin is null ? "true" : "false")}}, "user": {{(signedIn ? "\"admin\"" : "null")}}, "role": null, "demo": false}""");
                case "/cert.crt":
                    return new TestHttpServer.Response(200, CertificateProbe.ToPem(server.Certificate!.RawData), "application/x-pem-file");
            }
            if (!signedIn) return Json(401, """{"error": "Please sign in."}""");
            return path switch
            {
                "/api/provisioning" => Json(200, """
                    {"git": {"present": true, "detail": "git version 2.43.0"},
                     "docker": {"present": true, "detail": "26.1.0"},
                     "sglang_recipe": {"present": false, "dir": "/home/a/Qwen", "detail": "directory missing"},
                     "ssl": {"swapper_cert": true, "nginx_up": false, "nginx_configured": false, "port": 11443, "present": false},
                     "openclaw": {"present": false, "running": false, "detail": "not installed"}}
                    """),
                "/api/provisioning/job" => Json(200, """
                    {"job": {"id": "ab12", "target": "install:sglang_recipe", "source": null, "state": "running", "error": null,
                             "note": null, "started": 1727550000.0, "finished": null,
                             "steps": [{"key": "run", "label": "Installing", "state": "running", "started": 1727550000.0, "finished": null, "detail": ""}],
                             "log_total": 2, "log": [{"t": 1727550001.0, "m": "Target directory: /home/a/Qwen"}, {"t": 1727550002.0, "m": "Cloning …"}]}}
                    """),
                "/api/credentials" => Json(200, """
                    {"openclaw": {"token": "gw-token", "password": null, "auth_mode": "token", "dashboard_url": "https://192.168.1.42:11443/#token=gw-token"},
                     "api": {"key": "vllm-local", "base_url": "https://192.168.1.42:11443/v1", "model": "qwen3.8-27b-sglang",
                             "found_in": [], "other_keys": {}, "openclaw_provider_keys": []}}
                    """),
                _ => Json(404, """{"error": "not found"}"""),
            };
        }

        // POST: the service's same-origin rule.
        var host = request.Headers.GetValueOrDefault("Host") ?? "";
        if (request.Headers.GetValueOrDefault("X-Swapper") != "1"
            || request.Headers.TryGetValue("Origin", out var origin) && origin.Split("://", 2)[^1] != host)
            return Json(403, """{"error": "Cross-site request refused."}""");
        var body = JsonNode.Parse(request.Body.Length == 0 ? "{}" : request.Body) as JsonObject ?? new JsonObject();
        var user = body["username"]?.ToString() ?? "";
        var password = body["password"]?.ToString() ?? "";
        switch (path)
        {
            case "/api/setup":
                lock (_lock)
                {
                    if (_admin is not null) return Json(409, """{"error": "The admin login already exists."}""");
                    if (password.Length < 8) return Json(400, """{"error": "Use a password of at least 8 characters."}""");
                    _admin = (user, password);
                }
                return Json(200, $$"""{"ok": true, "user": "{{user}}"}""", SetCookie);
            case "/api/login":
                lock (_lock)
                {
                    if (_admin is { } a && a.User == user && a.Password == password)
                        return Json(200, """{"ok": true, "user": "admin", "role": "admin"}""", SetCookie);
                }
                return Json(401, """{"error": "That username and password don't match."}""");
        }
        if (!signedIn) return Json(401, """{"error": "Please sign in."}""");
        switch (path)
        {
            case "/api/install":
                var action = body["action"]?.ToString() ?? "";
                lock (_lock) Installs.Add(action);
                return Json(202, $$$"""{"ok": true, "job": {"id": "j{{{action}}}", "target": "install:{{{action}}}", "source": null, "state": "running", "error": null, "note": null, "started": 1.0, "finished": null, "steps": [], "log_total": 0, "log": []}}""");
            case "/api/swap":
                return Json(202, """{"ok": true, "job": {"id": "s1", "target": "standard", "source": null, "state": "running", "error": null, "note": null, "started": 1.0, "finished": null, "steps": [{"key": "prepare", "label": "Getting ready", "state": "running"}], "log": []}}""");
        }
        return Json(404, """{"error": "not found"}""");
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

public sealed class SwapperSetupTests
{
    [Fact]
    public async Task CreatesTheFirstAdminWithSameOriginHeaders()
    {
        await using var swapper = new FakeSwapper();
        using var client = new SparkSwapperClient(swapper.Url, "admin", "correct horse", swapper.Fingerprint);

        var before = await client.SessionAsync();
        Assert.True(before.SetupNeeded);
        Assert.Null(before.User);

        await client.SetupAdminAsync();
        var post = swapper.Server.Requests.Single(r => r.Path == "/api/setup");
        Assert.Equal("1", post.Headers["X-Swapper"]);
        Assert.Equal($"https://127.0.0.1:{swapper.Server.Port}", post.Headers["Origin"]);
        Assert.Contains("\"username\":\"admin\"", post.Body);

        // The session cookie from setup signs the client in for what follows.
        var after = await client.SessionAsync();
        Assert.False(after.SetupNeeded);
        Assert.Equal("admin", after.User);
        var credentials = await client.CredentialsAsync();
        Assert.Equal("vllm-local", credentials.ApiKey);
        Assert.DoesNotContain(swapper.Server.Requests, r => r.Path == "/api/login");
    }

    [Fact]
    public async Task SecondSetupIsRefused()
    {
        await using var swapper = new FakeSwapper(withAdmin: true);
        using var client = new SparkSwapperClient(swapper.Url, "me", "password123", swapper.Fingerprint);
        var error = await Assert.ThrowsAsync<SwapperException>(() => client.SetupAdminAsync());
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("The admin login already exists.", error.Detail);
    }

    [Fact]
    public async Task ShortPasswordsAreExplained()
    {
        await using var swapper = new FakeSwapper();
        using var client = new SparkSwapperClient(swapper.Url, "admin", "short", swapper.Fingerprint);
        var error = await Assert.ThrowsAsync<SwapperException>(() => client.SetupAdminAsync());
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Use a password of at least 8 characters.", error.Detail);
    }

    [Fact]
    public async Task ProvisioningInstallAndItsLog()
    {
        await using var swapper = new FakeSwapper(withAdmin: true);
        using var client = new SparkSwapperClient(swapper.Url, "admin", "correct horse", swapper.Fingerprint);

        var items = await client.ProvisioningAsync();
        Assert.Equal(["git", "docker", "sglang_recipe", "openclaw", "ssl"], items.Select(i => i.Action));
        Assert.True(items[0].Present);
        Assert.Equal("git version 2.43.0", items[0].Detail);
        Assert.False(items.Single(i => i.Action == "ssl").Present);
        Assert.Equal("Docker + NVIDIA GPU passthrough", items[1].Title);

        var job = await client.InstallAsync("sglang_recipe");
        Assert.Equal("install:sglang_recipe", job?.Target);
        Assert.Equal(["sglang_recipe"], swapper.Installs);

        var running = await client.ProvisioningJobAsync();
        Assert.NotNull(running);
        Assert.True(running.IsRunning);
        Assert.Equal(2, running.LogTotal);
        Assert.Equal(["Target directory: /home/a/Qwen", "Cloning …"], running.Log!.Select(l => l.M));
        Assert.Equal("Installing", running.CurrentStep?.Label);

        var swap = await client.StartSwapAsync("standard");
        Assert.Equal("standard", swap?.Target);
    }

    [Fact]
    public async Task CredentialsAndCertificate()
    {
        await using var swapper = new FakeSwapper(withAdmin: true);
        using var client = new SparkSwapperClient(swapper.Url, "admin", "correct horse", swapper.Fingerprint);
        var credentials = await client.CredentialsAsync();
        Assert.Equal("vllm-local", credentials.ApiKey);
        Assert.Equal("https://192.168.1.42:11443/v1", credentials.ApiBaseUrl);
        Assert.Equal("qwen3.8-27b-sglang", credentials.ApiModel);
        Assert.Equal("gw-token", credentials.GatewayToken);

        using var certificate = await client.DownloadCertificateAsync();
        Assert.Equal(swapper.Fingerprint, SparkSwapperClient.Fingerprint(certificate.RawData));
    }

    [Fact]
    public async Task WrongPasswordIsABadLogin()
    {
        await using var swapper = new FakeSwapper(withAdmin: true);
        using var client = new SparkSwapperClient(swapper.Url, "admin", "nope nope", swapper.Fingerprint);
        var error = await Assert.ThrowsAsync<SwapperException>(() => client.ProvisioningAsync());
        Assert.Equal(SwapperErrorKind.BadLogin, error.Kind);
        Assert.Equal("That username and password don't match.", error.Detail);
    }

    [Fact]
    public async Task UnpinnedSelfSignedCertificateIsReportedWithItsFingerprint()
    {
        await using var swapper = new FakeSwapper();
        using var client = new SparkSwapperClient(swapper.Url, "admin", "correct horse", null);
        var error = await Assert.ThrowsAsync<SwapperException>(() => client.SessionAsync());
        Assert.Equal(SwapperErrorKind.UntrustedCertificate, error.Kind);
        Assert.Equal(swapper.Fingerprint, error.Fingerprint);
        Assert.Equal(swapper.Fingerprint, await SparkSwapperClient.ProbeFingerprintAsync(swapper.Url));
    }

    [Fact]
    public void OriginsMatchTheHostHeader()
    {
        Assert.Equal("https://192.168.1.42:8999", SparkSwapperClient.OriginFor(new Uri("https://192.168.1.42:8999/")));
        Assert.Equal("https://spark-3f2a.local:8999", SparkSwapperClient.OriginFor(new Uri("https://spark-3f2a.local:8999")));
    }

    // MARK: - Following a log

    private static SwapperLogLine L(double t, string m) => new(t, m);

    [Fact]
    public void LogTailFindsOnlyNewLines()
    {
        var first = new List<SwapperLogLine> { L(1, "a"), L(2, "b"), L(3, "c") };
        Assert.Equal(first, SwapperLogTail.NewLines([], first));
        // The window slid by one and two lines arrived.
        var second = new List<SwapperLogLine> { L(2, "b"), L(3, "c"), L(4, "d"), L(5, "e") };
        Assert.Equal([L(4, "d"), L(5, "e")], SwapperLogTail.NewLines(first, second));
        // Nothing new.
        Assert.Empty(SwapperLogTail.NewLines(second, second));
        // No overlap at all: everything is new.
        Assert.Equal([L(9, "x")], SwapperLogTail.NewLines(second, [L(9, "x")]));
    }

    [Fact]
    public void LogTailHandlesRepeatedLinesAndNewJobs()
    {
        // The same text at the same time twice (one multi-line message) must not confuse the overlap.
        var first = new List<SwapperLogLine> { L(1, "x"), L(1, "x") };
        var second = new List<SwapperLogLine> { L(1, "x"), L(1, "x"), L(1, "x") };
        Assert.Equal([L(1, "x")], SwapperLogTail.NewLines(first, second));

        var tail = new SwapperLogTail();
        SwapperJob Job(string id, params SwapperLogLine[] lines) => new(id, "t", null, "running", null, null, 0, null, null, lines);
        Assert.Equal(["a"], tail.Next(Job("1", L(1, "a"))).Select(l => l.M));
        Assert.Equal(["b"], tail.Next(Job("1", L(1, "a"), L(2, "b"))).Select(l => l.M));
        Assert.Equal(["a"], tail.Next(Job("2", L(1, "a"))).Select(l => l.M)); // a new job starts over
        Assert.Empty(tail.Next(null));
    }

    [Fact]
    public void ParsesCredentialsWithMissingParts()
    {
        var empty = SparkSwapperClient.ParseCredentials(new JsonObject());
        Assert.Null(empty.ApiKey);
        Assert.Null(empty.GatewayToken);
    }
}
